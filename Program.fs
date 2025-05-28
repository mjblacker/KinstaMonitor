open System
open System.Threading
open KinstaMonitor
open Types

// Program entry point
[<EntryPoint>]
let main _ =
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
