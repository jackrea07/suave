
#nowarn "40"
#nowarn "3370"

namespace Suave

open System
open System.IO
open System.Collections.Generic
open System.Text

open Suave
open Suave.Utils
open Suave.Utils.Parsing
open Suave.Logging
open Suave.Logging.Message
open Suave.Sockets
open Suave.Sockets.Control
open Suave.Sockets.SocketOp.Operators
open Suave.Utils.Bytes
open System.IO.Pipelines
open Suave.Utils.AsyncExtensions
open System.Buffers
open System.Threading
open System.Threading.Tasks

[<Struct>]
type ScanResult =
  NeedMore | Found of found:int | Error of Error

[<Struct>]
type SelectResult =
  FailWith of error:Error | Continue of int

type SelectFunction = ReadOnlyMemory<byte> -> int -> SelectResult

module Aux =
  /// Splits the segment list in two lits of ArraySegment; the first one containing a total of index bytes
  let inline split (bufferSequence:ReadOnlySequence<byte>) index (select:SelectFunction) : SelectResult =
    let rec loop  (bufferSequence:ReadOnlySequence<byte>) acc selectResult : SelectResult = 
      match selectResult with
      | Continue count ->
        if bufferSequence.Length = 0 then
          Continue count
        else
          let pair = bufferSequence.First
          if acc + pair.Length < index then
            let selectResult = select pair pair.Length
            match selectResult with
            | Continue _ ->
              loop (bufferSequence.Slice(pair.Length)) (acc + pair.Length) (Continue(count + acc + pair.Length))
            | FailWith s ->
              FailWith s
          elif acc + pair.Length >= index then
            let bytesRead = index - acc
            let selectResult = select pair bytesRead
            match selectResult with
            | Continue _ ->
              Continue(count + bytesRead)
              | FailWith s ->
                FailWith s
            else failwith "Suave.Web.split: invalid case"
      | FailWith s ->
        FailWith s
    loop bufferSequence 0 (Continue 0)

type Reader(transport : TcpTransport, lineBuffer : byte array, pipe: Pipe) =

  member (*inline*) x.readMoreData () = task {
    let buff = pipe.Writer.GetMemory()
    match! transport.read buff with
    | Ok x ->
      pipe.Writer.Advance(x)
      let! flushResult = pipe.Writer.FlushAsync()
      return Ok()
    | Result.Error error ->
      return Result.Error error
    }

  member (*inline*) x.getData () = task{
      let (success, result) = pipe.Reader.TryRead()
      if success then
        return result
      else
        let! result = x.readMoreData()
        let! result= pipe.Reader.ReadAsync()
        return result
  }
  /// Iterates over a BufferSegment list looking for a marker, data before the marker
  /// is sent to the function select
  /// Returns the number of bytes read.
  member (*inline*) x.scanMarker (marker: byte[]) (select : SelectFunction) = 
    task{
      let! result = x.getData()
      let bufferSequence = result.Buffer
      match kmpW marker bufferSequence with
      | ValueSome x ->
        let res = Aux.split bufferSequence (int x) select
        match res with
        | Continue n ->
          pipe.Reader.AdvanceTo(bufferSequence.GetPosition(int64(n + marker.Length)))
          return Result.Ok(Found n)
        | FailWith s ->
          return Result.Ok(Error s)
      | ValueNone ->
        let r = Aux.split bufferSequence (int(bufferSequence.Length - int64 marker.Length)) select
        pipe.Reader.AdvanceTo(bufferSequence.GetPosition(bufferSequence.Length - int64 marker.Length))
        return Result.Ok(NeedMore)
  }

  /// Read the passed stream into buff until the EOL (CRLF) has been reached
  /// and returns the number of bytes read and the connection
  member (*inline*) x.readUntilPattern marker select =
    task {
      let reading = ref true
      let error = ref false
      let result = ref 0
      let errorResult = ref (Ok(0))
      while !reading && not !error do
        let! res = x.scanMarker marker select
        match res with
        | Ok(Found a) ->
          reading := false
          result := a
        | Ok(NeedMore) ->
          let! a = x.readMoreData()
          // continue reading
          ()
        | Result.Error s
        | Ok(Error s) ->
          error := true
          errorResult := Result.Error s
      if !error then
        return !errorResult
      else
        return Ok (!result)
    }

  member x.skip n =
     task{
      let! result= pipe.Reader.ReadAsync()
      let bufferSequence = result.Buffer
      // we really do not calling split because we are not doing anything with it
      let res = Aux.split bufferSequence n (fun a b -> Continue 0 )
      match res with
        | Continue n ->
          pipe.Reader.AdvanceTo(bufferSequence.GetPosition(n))
          return Found n
        | FailWith s ->
          return Error s
      }

  /// Read a line from the stream, calling UTF8.toString on the bytes before the EOL marker
  member (*inline*) x.readLine () = socket {
    let offset = ref 0
    let! count =
      x.readUntilPattern EOL (fun a count ->
        if !offset + count > lineBuffer.Length then
          FailWith (InputDataError (Some 414, "Line Too Long"))
        else
          let source = a.Span.Slice(0,count)
          let target = new Span<byte>(lineBuffer,!offset,count)
          source.CopyTo(target)
          offset := !offset + count
          
          Continue !offset
      )
    let result = Encoding.UTF8.GetString(lineBuffer, 0, !offset)
    return result
  }

  member x.skipLine ()= socket {
    let offset = ref 0
    let! _ =
      x.readUntilPattern EOL (fun a count ->
        offset := !offset + count
        Continue !offset
      )
    return offset
  }

  /// Read all headers from the stream, returning a dictionary of the headers found
  member (*inline*) x.readHeaders() =
    task {
      let headers = new List<string*string>()
      let flag = ref true
      let error = ref false
      let result = ref (Ok ([]))
      while !flag && (not !error)do
        let! _line = x.readLine ()
        match _line with
        | Ok line ->
          if line <> String.Empty then
            let indexOfColon = line.IndexOf(':')
            let header = (line.Substring(0, indexOfColon).ToLower(), line.Substring(indexOfColon+1).TrimStart())
            headers.Add header
          else
            flag := false
        | Result.Error e ->
          error := true
          result := Result.Error e

      if !error then
          return !result
        else
          return Ok(List.ofSeq headers)
    }

  /// Read the post data from the stream, given the number of bytes that makes up the post data.
  member (*inline*) x.readPostData (bytes : int) (select:ReadOnlyMemory<byte> -> int -> unit)  : SocketOp<unit> =
    let rec loop n : SocketOp<unit> =
      task {
        if n = 0 then
          return Result.Ok()
        else
          let! result = x.getData()
          let bufferSequence = result.Buffer
          if bufferSequence.Length > 0  then
            let segment = bufferSequence.First
            if segment.Length > n then
              select segment n
              pipe.Reader.AdvanceTo(bufferSequence.GetPosition(int64(n)))
              return Result.Ok()
            else
              select segment segment.Length
              pipe.Reader.AdvanceTo(bufferSequence.GetPosition(int64(segment.Length)))
              return! loop (n - segment.Length)
          else
            if n = 0 then
              return Result.Ok()
            else
              match! x.readMoreData() with
              | Result.Ok _ ->
                return! loop n
              | Result.Error error ->
                return Result.Error error
      }
    loop bytes

type ConnectionFacade(connection: Connection, runtime: HttpRuntime, logger:Logger, connectionPool: ConcurrentPool<ConnectionFacade>,webpart:WebPart) =

  let reader = new Reader(connection.transport,connection.lineBuffer,connection.pipe)

  let httpOutput = new HttpOutput(connection,runtime)

  let files = List<HttpUpload>()
  let multiPartFields = List<string*string>()
  let mutable _rawForm : byte array = [||]

  let readFilePart boundary (headerParams : Dictionary<string,string>) fieldName contentType = socket {
    let tempFilePath = Path.GetTempFileName()
    use tempFile = new FileStream(tempFilePath, FileMode.Truncate)
    let! a =
      reader.readUntilPattern (ASCII.bytes (eol + boundary)) (fun x y ->
          do tempFile.Write(x.Span.Slice(0,y))
          Continue 0)
    let fileLength = tempFile.Length
    tempFile.Dispose()

    if fileLength > 0L then
      let! filename =
        match headerParams.TryLookup "filename*" with
        | Choice1Of2 _filename ->
          let ix = _filename.IndexOf "''"
          if ix > 0 then
            let enc = _filename.Substring(0,ix).ToLowerInvariant()
            if enc = "utf-8" then
              let filename = Net.WebUtility.UrlDecode(_filename.Substring(ix + 2))
              SocketOp.mreturn (filename)
            else
              SocketOp.abort (InputDataError (None, "Unsupported filename encoding: '" + enc + "'"))
          else
            SocketOp.abort (InputDataError (None, "Invalid filename encoding"))
        | Choice2Of2 _ ->
          (headerParams.TryLookup "filename" |> Choice.map (String.trimc '"'))
          @|! (None, "Key 'filename' was not present in 'content-disposition'")

      let upload =
        { fieldName    = fieldName
          fileName     = filename
          mimeType     = contentType
          tempFilePath = tempFilePath }

      return Some upload
    else
      File.Delete tempFilePath
      return None
    }

  let parseMultipartMixed fieldName boundary : SocketOp<unit> =
    let rec loop () = socket {
      let! firstLine = reader.readLine()

      if firstLine.Equals("--") then
        return ()
      else
        let! partHeaders = reader.readHeaders()

        let! (contentDisposition : string) =
          (partHeaders %% "content-disposition")
          @|! (None, "Missing 'content-disposition'")

        match partHeaders %% "content-type" with
        | Choice1Of2 contentType ->
          let headerParams = headerParams contentDisposition
          logger.verbose (eventX "Parsing {contentType}... -> readFilePart" >> setFieldValue "contentType" contentType)

          let! res = readFilePart boundary headerParams fieldName contentType
          logger.verbose (eventX "Parsed {contentType} <- readFilePart" >> setFieldValue "contentType" contentType)

          match res with
          | Some upload ->
            files.Add(upload)
            return! loop ()
          | None ->
            return! loop ()

        | Choice2Of2 _ ->
          use mem = new MemoryStream()
          let! a =
            reader.readUntilPattern (ASCII.bytes(eol + boundary)) (fun x y -> 
                mem.Write(x.Span.Slice(0,y))
                Continue 0
              )
          let byts = mem.ToArray()
          multiPartFields.Add (fieldName, Encoding.UTF8.GetString(byts, 0, byts.Length))
          return! loop ()
      }
    loop ()

  /// Parses multipart data from the stream, feeding it into the HttpRequest's property Files.
  let parseMultipart (boundary:string) : SocketOp<unit> =
    let parsePart () = socket {
        let! partHeaders = reader.readHeaders()
        let! (contentDisposition : string) =
          (partHeaders %% "content-disposition")
          @|! (None, "Missing 'content-disposition'")

        let headerParams = headerParams contentDisposition

        let! _ =
          (headerParams.TryLookup "form-data" |> Choice.map (String.trimc '"'))
          @|! (None, "Key 'form-data' was not present in 'content-disposition'")

        let! fieldName =
          (headerParams.TryLookup "name" |> Choice.map (String.trimc '"'))
          @|! (None, "Key 'name' was not present in 'content-disposition'")

        match partHeaders %% "content-type" with
        | Choice1Of2 x when String.startsWith "multipart/mixed" x ->
          let subboundary = "--" + parseBoundary x
          do! parseMultipartMixed fieldName subboundary
          let a = reader.skip (boundary.Length)
          return ()

        | Choice1Of2 contentType when headerParams.ContainsKey "filename" ->
          logger.verbose (
            eventX "Parsing {contentType}... -> readFilePart"
            >> setFieldValue "contentType" contentType
            >> setSingleName "Suave.Web.parseMultipart")
          let! res = readFilePart boundary headerParams fieldName contentType
          logger.verbose (
            eventX "Parsed {contentType} <- readFilePart"
            >> setFieldValue "contentType" contentType
            >> setSingleName "Suave.Web.parseMultipart")

          res |> Option.iter files.Add

        | Choice1Of2 _ | Choice2Of2 _ ->
          use mem = new MemoryStream()
          let! a =
            reader.readUntilPattern (ASCII.bytes (eol + boundary)) (fun x y ->
              mem.Write(x.Span.Slice(0,y))
              Continue 0
            )
          let byts = mem.ToArray()
          let str =  Encoding.UTF8.GetString(byts, 0, byts.Length)
          multiPartFields.Add(fieldName,str)
          return ()
      }

    let something () = socket{
        do! parsePart ()
        let! line = reader.readLine()
        return line
    }

    let firstPart(boundary) = socket{
      let! firstLine = reader.readLine()
      if firstLine<>boundary then
        failwithf "Invalid multipart format: expected boundary '%s' got '%s'" boundary firstLine
    }

    let secondPart () = task{
      let mutable parsing = true
      let mutable error = false
      let result = ref (Ok())
      while parsing && not error do
        let! _line = something ()
        match _line with
        | Ok line ->
          if line.StartsWith("--") then
            parsing <- false
          else if line <> String.Empty then
            failwith "Invalid multipart format"
        | Result.Error e ->
          result := Result.Error e
          error <- true
      if error then
        return !result
      else
        return Ok()
    }

    socket {
      do! firstPart (boundary)
      do! secondPart ()
      return ()
    }

  /// Reads raw POST data
  let getRawPostData contentLength =
    socket {
      let offset = ref 0
      let rawForm = Array.zeroCreate contentLength
      do! reader.readPostData contentLength (fun a count ->
          let source = a.Span.Slice(0,count)
          let target = new Span<byte>(rawForm,!offset,count)
          source.CopyTo(target)
          offset := !offset + count)
      return rawForm
    }

  member val Connection = connection with get,set
  member val Runtime = runtime with get,set

  member (*inline*) this.parsePostData maxContentLength (contentLengthHeader : Choice<string,_>) (contentTypeHeader:Choice<string,_>) = socket {
    match contentLengthHeader with
    | Choice1Of2 contentLengthString ->
      let contentLength = Convert.ToInt32 contentLengthString

      if contentLength > maxContentLength then
        return! SocketOp.abort(InputDataError (Some 413, "Payload too large"))
      else
        logger.verbose (eventX "Expecting {contentLength} bytes" >> setFieldValue "contentLength" contentLength)

        match contentTypeHeader with
        | Choice1Of2 ce when String.startsWith "application/x-www-form-urlencoded" ce ->
          let! rawForm = getRawPostData contentLength
          _rawForm <- rawForm
          return ()

        | Choice1Of2 ce when String.startsWith "multipart/form-data" ce ->
          let boundary = "--" + parseBoundary ce

          logger.verbose (eventX "Parsing multipart")
          do! parseMultipart boundary
          logger.verbose (eventX "Done parsing multipart")
          return ()

        | Choice1Of2 _ | Choice2Of2 _ ->
          let! rawForm = getRawPostData contentLength
          _rawForm <- rawForm
          return ()
      | Choice2Of2 _ -> return ()
    }

  /// Process the request, reading as it goes from the incoming 'stream', yielding a HttpRequest
  /// when done
  member (*inline*) this.processRequest () = socket {

    logger.verbose (eventX  "reading first line of request")
    let! firstLine = reader.readLine()

    let! rawMethod, path, rawQuery, httpVersion = 
      parseUrl firstLine
      @|! (None, "Invalid ")

    logger.verbose (eventX "reading headers")
    let! headers = reader.readHeaders()

    // Respond with 400 Bad Request as
    // per http://www.w3.org/Protocols/rfc2616/rfc2616-sec14.html
    let! rawHost = headers %% "host" @|! (None, "Missing 'Host' header")

    if headers %% "expect" = Choice1Of2 "100-continue" then
      let! _ = httpOutput.run HttpRequest.empty Intermediate.CONTINUE
      logger.verbose (eventX "sent 100-continue response")

    logger.verbose (eventX "parsing post data")
    do! this.parsePostData runtime.maxContentLength (headers %% "content-length") (headers %% "content-type")

    let request =
      { httpVersion      = httpVersion
        binding          = runtime.matchedBinding
        rawPath          = path
        rawHost          = rawHost
        rawMethod        = rawMethod
        headers          = headers
        rawForm          = _rawForm
        rawQuery         = rawQuery
        files            = Seq.toList files
        multiPartFields  = Seq.toList multiPartFields
        trace            = TraceHeader.parseTraceHeaders headers }
    
    // clear form data before exit
    files.Clear()
    multiPartFields.Clear()
    _rawForm <- [||]

    return Some request
  }

  member this.exitHttpLoopWithError (err:Error) = task{
      match err with
      | InputDataError (None, msg) ->
        logger.verbose (eventX "Error parsing HTTP request with {message}" >> setFieldValue "message" msg)
        match! httpOutput.run HttpRequest.empty (RequestErrors.BAD_REQUEST msg) with
        | _ ->
          logger.verbose (eventX "Exiting http loop")

      | InputDataError (Some status,msg) ->
        logger.verbose (eventX "Error parsing HTTP request with {message}" >> setFieldValue "message" msg)
        match Http.HttpCode.tryParse status with 
        | (Choice1Of2 statusCode) ->
          match! httpOutput.run HttpRequest.empty (Response.response statusCode (Encoding.UTF8.GetBytes msg)) with
          | _ -> logger.verbose (eventX "Exiting http loop")
        | (Choice2Of2 err) ->
          logger.warn (eventX "Invalid HTTP status code {statusCode}" >> setFieldValue "statusCode" status)
          match! httpOutput.run HttpRequest.empty (RequestErrors.BAD_REQUEST msg) with
          | _ ->
            logger.verbose (eventX "Exiting http loop")
      | err ->
        logger.verbose (eventX "Socket error while processing request, exiting {error}" >> setFieldValue "error" err)
      return Ok(false)
    }

  member this.loop  =
    task {
      logger.verbose (eventX "Processing request... -> processor")
      let! result' = this.processRequest ()
      logger.verbose (eventX "Processed request. <- processor")
      match result' with
      | Ok result ->
        match result with
        | None ->
          logger.verbose (eventX "'result = None', exiting")
          return Ok (false)
        | Some request ->
          match! httpOutput.run request webpart  with
          | Result.Error err -> return Result.Error err 
          | Ok keepAlive ->
              if keepAlive then
                logger.verbose (eventX "'Connection: keep-alive' recurse")
                return Ok (keepAlive)
              else
                logger.verbose (eventX "Connection: close")
                return Ok(false)
      | Result.Error err ->
        // Couldn't parse HTTP request; answering with BAD_REQUEST and closing the connection.
        return! this.exitHttpLoopWithError err
    }

  member this.shutdown() =
    connection.lineBufferCount <- 0
    connection.transport.shutdown()
    connectionPool.Push(this)

  /// The request loop initialises a request with a processor to handle the
  /// incoming stream and possibly pass the request to the web parts, a protocol,
  /// a web part, an error handler and a Connection to use for read-write
  /// communication -- getting the initial request stream.
  member inline this.requestLoop =
    task {
      let flag = ref true
      while !flag do
        let! b = this.loop
        match b with
        | Ok b -> flag := b
        | _ -> flag := false
      return ()
      }

  member this.accept(binding) = task{
    Interlocked.Increment Globals.numberOfClients |> ignore
    logger.verbose (eventX "{client} connected. Now has {totalClients} connected"
      >> setFieldValue "client" (binding.ip.ToString())
      >> setFieldValue "totalClients" (!Globals.numberOfClients))
    connection.socketBinding <- binding
    try
      do! this.requestLoop
    with
      | :? System.IO.EndOfStreamException ->
        logger.debug (eventX "Disconnected client (end of stream)")
    logger.verbose (eventX "Shutting down transport")
    this.shutdown()
    Interlocked.Decrement(Globals.numberOfClients) |> ignore
    logger.verbose (eventX "Disconnected {client}. {totalClients} connected."
      >> setFieldValue "client" (binding.ip.ToString())
      >> setFieldValue "totalClients" (!Globals.numberOfClients))
  }
