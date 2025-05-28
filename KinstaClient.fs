namespace KinstaMonitor

open System
open System.Net.Http
open System.Text.Json
open KinstaMonitor.Types

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
                        JsonSerializer.Deserialize<RestartResponse>(responseContent, options) |> Option.ofObj

                    return
                        match restartResponse with
                        | Some restartResponse -> Ok restartResponse
                        | _ -> Error $"Response failed to deserialize"
                else
                    let! errorContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return Error $"Failed to restart PHP. Status: {response.StatusCode}, Content: {errorContent}"
            with ex ->
                return Error $"Exception while restarting PHP: {ex.Message}"
        }

    interface IDisposable with
        member this.Dispose() = httpClient.Dispose()

