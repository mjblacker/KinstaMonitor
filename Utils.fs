namespace KinstaMonitor

open System
open System.IO
open System.Text.Json
open Types

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
                let dateStr = parts[0]
                let timeStr = parts[1]
                let timestampStr = $"{dateStr} {timeStr}"
                let remainingMessage = if parts.Length > 2 then parts[2] else ""

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

    let loadConfig (configPath: string) : Config option =
        try
            if File.Exists(configPath) then
                let json = File.ReadAllText(configPath)
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                match JsonSerializer.Deserialize<Config>(json, options) with
                | Null -> None
                | NonNull config -> Some config
            else
                None
        with ex ->
            printfn $"Error loading config: {ex.Message}"
            None

    let createDefaultConfig () =
        { ApiKey = "your-kinsta-api-key-here"
          EnvId = "your-environment-id-here"
          TriggerString = "upstream timed out (110: Connection timed out)"
          CheckIntervalMinutes = 5
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

