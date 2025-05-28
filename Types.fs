module KinstaMonitor.Types

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

