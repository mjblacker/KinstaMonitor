open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic

// Configuration types
type Config =
    { ApiKey: string
      EnvId: string
      TriggerString: string
      CheckIntervalMinutes: int
      LogFileName: string }

// API response types
type LogEntry =
    { timestamp: string
      message: string
      fullLine: string }

type ContainerInfo = { logs: string }

type Environment = { container_info: ContainerInfo }

type LogResponse = { environment: Environment }


type RestartResponse =
    { message: string
      status: int
      operation_id: string option }

// Service state to track processed logs
type ServiceState =
    { mutable LastProcessedTimestamp: DateTime option
      mutable ProcessedLogHashes: Set<string>
      mutable LastCheckTime: DateTime option }

// Utility functions
module Utils =
    let parseTimestamp (timestampStr: string) =
        match DateTime.TryParse(timestampStr) with
        | true, dt -> Some dt
        | false, _ -> None

    let parseLogLine (line: string) =
        if String.IsNullOrWhiteSpace(line) then
            None
        else
            // Try to extract timestamp from the beginning of the line
            // Format appears to be: "2025/05/26 08:10:32 [error] ..."
            let parts = line.Split(' ', 3)

            if parts.Length >= 3 then
                let dateStr = parts.[0]
                let timeStr = parts.[1]
                let timestampStr = $"{dateStr} {timeStr}"
                let remainingMessage = if parts.Length > 2 then parts.[2] else ""

                Some
                    { timestamp = timestampStr
                      message = remainingMessage
                      fullLine = line }
            else
                // If we can't parse timestamp, treat the whole line as message
                Some
                    { timestamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")
                      message = line
                      fullLine = line }

    let parseLogsFromResponse (logsString: string) =
        logsString.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose parseLogLine

    let getLogHash (log: LogEntry) =
        // Use the full line for hashing to ensure uniqueness
        log.fullLine.GetHashCode().ToString()

    let loadConfig (configPath: string) =
        try
            if File.Exists(configPath) then
                let json = File.ReadAllText(configPath)
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                Some(JsonSerializer.Deserialize<Config>(json, options))
            else
                None
        with ex ->
            printfn $"Error loading config: {ex.Message}"
            None

    let createDefaultConfig () =
        { ApiKey = "your-kinsta-api-key-here"
          EnvId = "your-environment-id-here"
          TriggerString = "upstream timed out (110: Connection timed out)"
          CheckIntervalMinutes = 1
          LogFileName = "error" }

    let saveConfig (config: Config) (configPath: string) =
        try
            let json =
                JsonSerializer.Serialize(config, JsonSerializerOptions(WriteIndented = true))

            File.WriteAllText(configPath, json)
            true
        with ex ->
            printfn $"Error saving config: {ex.Message}"
            false


// HTTP client wrapper for Kinsta API
type KinstaApiClient(apiKey: string) =
    let httpClient = new HttpClient()

    do
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}")
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Kinsta-Log-Monitor/1.0")

    let logInfo message =
        let timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        printfn $"[{timestamp}] INFO: {message}"

    member this.GetLogsAsync(envId: string, fileName: string, lines: int) =
        async {
            try
                let url =
                    $"https://api.kinsta.com/v2/sites/environments/{envId}/logs?file_name={fileName}&lines={lines}"

                let! response = httpClient.GetAsync(url) |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    let! content = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    let options = JsonSerializerOptions()
                    options.PropertyNameCaseInsensitive <- true

                    let logResponse = JsonSerializer.Deserialize<LogResponse>(content, options)

                    match logResponse with
                    | Null -> return Error $"Failed to parse the data"
                    | NonNull logResponse ->
                        let parsedLogs =
                            Utils.parseLogsFromResponse logResponse.environment.container_info.logs
                        return Ok parsedLogs
                else
                    let! errorContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return Error $"Failed to fetch logs. Status: {response.StatusCode}, Content: {errorContent}"
            with ex ->
                return Error $"Exception while fetching logs: {ex.Message}"
        }

    member this.RestartPhpAsync(envId: string) =
        async {
            try

                let url = "https://api.kinsta.com/v2/sites/tools/restart-php"
                let payload = JsonSerializer.Serialize({| environment_id = envId |})

                let content =
                    new StringContent(payload, System.Text.Encoding.UTF8, "application/json")

                let! response = httpClient.PostAsync(url, content) |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    let options = JsonSerializerOptions()
                    options.PropertyNameCaseInsensitive <- true

                    let restartResponse =
                        JsonSerializer.Deserialize<RestartResponse>(responseContent, options)

                    return Ok restartResponse
                else
                    let! errorContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return Error $"Failed to restart PHP. Status: {response.StatusCode}, Content: {errorContent}"
            with ex ->
                return Error $"Exception while restarting PHP: {ex.Message}"
        }

    interface IDisposable with
        member this.Dispose() = httpClient.Dispose()


// Main monitoring service
type LogMonitorService(config: Config) =
    let apiClient = new KinstaApiClient(config.ApiKey)

    let state =
        { LastProcessedTimestamp = None
          ProcessedLogHashes = Set.empty
          LastCheckTime = None }

    let logInfo message =
        let timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        printfn $"[{timestamp}] INFO: {message}"

    let logError message =
        let timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        printfn $"[{timestamp}] ERROR: {message}"

    let logWarning message =
        let timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        printfn $"[{timestamp}] WARNING: {message}"

    member this.ProcessLogsAsync() =
        async {
            logInfo "Checking logs..."

            match! apiClient.GetLogsAsync(config.EnvId, config.LogFileName, 20) with
            | Ok logs ->
                logInfo $"Retrieved {logs.Length} log entries"

                // Filter out already processed logs
                let newLogs =
                    logs
                    |> Array.filter (fun log ->
                        let hash = Utils.getLogHash log
                        not (state.ProcessedLogHashes.Contains(hash)))

                if newLogs.Length > 0 then
                    logInfo $"Found {newLogs.Length} new log entries to process"

                    // Check for trigger string in new logs
                    let triggeredLogs =
                        newLogs |> Array.filter (fun log -> log.message.Contains(config.TriggerString))

                    // only restart if more than two of them in the new logs
                    if triggeredLogs.Length >= 2 then
                        logWarning $"Found {triggeredLogs.Length} log entries containing trigger string: '{config.TriggerString}'"

                        // Print the triggered log entries
                        for log in triggeredLogs do
                            logWarning $"Triggered log: [{log.timestamp}] {log.message}"

                        // Restart PHP (only once per check)
                        logInfo "Initiating PHP restart..."

                        match! apiClient.RestartPhpAsync(config.EnvId) with
                        | Ok response ->
                            logInfo $"PHP restart initiated successfully: {response.message}"

                            match response.operation_id with
                            | Some opId -> logInfo $"Operation ID: {opId}"
                            | None -> ()
                        | Error error -> logError $"Failed to restart PHP: {error}"
                    else
                        logInfo "No trigger string found in new logs"

                    // Update processed logs state
                    for log in newLogs do
                        let hash = Utils.getLogHash log
                        state.ProcessedLogHashes <- state.ProcessedLogHashes.Add(hash)

                    // Keep only recent hashes to prevent memory growth
                    if state.ProcessedLogHashes.Count > 1000 then
                        let recentHashes =
                            state.ProcessedLogHashes |> Set.toArray |> Array.skip 500 |> Set.ofArray

                        state.ProcessedLogHashes <- recentHashes
                        logInfo "Cleaned up old processed log hashes"
                else
                    logInfo "No new log entries to process"

            | Error error -> logError error
        }

    member this.StartAsync(cancellationToken: CancellationToken) =
        async {
            logInfo $"Starting log monitor service for environment: {config.EnvId}"
            logInfo $"Monitoring for string: '{config.TriggerString}'"
            logInfo $"Check interval: {config.CheckIntervalMinutes} minutes"

            let checkIntervalMs = config.CheckIntervalMinutes * 60 * 1000
            let sleepIntervalMs = 1000 // Check every second for cancellation

            while not cancellationToken.IsCancellationRequested do
                try
                    let now = DateTime.Now

                    let shouldCheck =
                        match state.LastCheckTime with
                        | None -> true // First run
                        | Some lastCheck ->
                            let timeSinceLastCheck = now - lastCheck
                            timeSinceLastCheck.TotalMilliseconds >= float checkIntervalMs

                    if shouldCheck then
                        state.LastCheckTime <- Some now
                        do! this.ProcessLogsAsync()

                    // Short sleep to be responsive to cancellation
                    do! Async.Sleep(sleepIntervalMs)

                with
                | :? OperationCanceledException -> logInfo "Service cancellation requested"
                | ex ->
                    logError $"Unexpected error in monitoring loop: {ex.Message}"
                    do! Async.Sleep(5000) // Wait 5 seconds before retrying on error
        }


    interface IDisposable with
        member this.Dispose() = (apiClient :> IDisposable).Dispose()

// Program entry point
[<EntryPoint>]
let main args =
    let configPath = "kinsta-monitor-config.json"

    // Load or create configuration
    let config =
        match Utils.loadConfig configPath with
        | Some cfg -> cfg
        | None ->
            printfn "No configuration file found. Creating default configuration..."
            let defaultConfig = Utils.createDefaultConfig ()

            if Utils.saveConfig defaultConfig configPath then
                printfn $"Default configuration saved to {configPath}"
                printfn "Please edit the configuration file with your API key and environment details, then restart the service."
                exit 1
            else
                printfn "Failed to create default configuration file"
                exit 1

    // Validate configuration
    if
        config.ApiKey = "your-kinsta-api-key-here"
        || config.EnvId = "your-environment-id-here"
    then
        printfn "Please configure your API key and environment ID in the configuration file"
        exit 1

    printfn "Kinsta Log Monitor Service"
    printfn "========================="
    printfn $"Environment ID: {config.EnvId}"
    printfn $"Trigger String: {config.TriggerString}"
    printfn $"Check Interval: {config.CheckIntervalMinutes} minutes"
    printfn $"Log File: {config.LogFileName}"
    printfn ""
    printfn "Press Ctrl+C to stop the service"
    printfn ""

    use cancellationTokenSource = new CancellationTokenSource()

    // Handle Ctrl+C gracefully
    Console.CancelKeyPress.Add(fun args ->
        if args.Cancel then
            failwith "forced shutdown"

        args.Cancel <- true
        cancellationTokenSource.Cancel()
        printfn "\nShutdown requested...")

    // Start the monitoring service
    use service = new LogMonitorService(config)

    try
        service.StartAsync(cancellationTokenSource.Token) |> Async.RunSynchronously

        printfn "Service stopped successfully"
        0
    with
    | :? OperationCanceledException ->
        printfn "Service stopped by user"
        0
    | ex ->
        printfn $"Service failed with error: {ex.Message}"
        1
