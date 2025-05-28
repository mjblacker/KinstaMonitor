namespace KinstaMonitor

open System
open System.Threading
open Types

// Service state to track processed logs
type ServiceState =
    { mutable LastProcessedTimestamp: DateTime option
      mutable ProcessedLogHashes: Set<string>
      mutable LastCheckTime: DateTime option }

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
