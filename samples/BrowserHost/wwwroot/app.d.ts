export type PowerShellWasmStream =
  | "Output"
  | "Debug"
  | "Error"
  | "Host"
  | "Information"
  | "Progress"
  | "Verbose"
  | "Warning"
  | string;

export interface PowerShellWasmOutputRecord {
  stream: PowerShellWasmStream;
  text: string;
}

export interface PowerShellWasmResult {
  text: string;
  records: PowerShellWasmOutputRecord[];
}

export interface PowerShellWasmSession {
  id: string;
  execute(script: string): Promise<string>;
  executeResult(script: string): Promise<PowerShellWasmResult>;
  dispose(): Promise<boolean>;
}

export interface PowerShellWasmOptions {
  environment?: Record<string, string>;
  session?: string | PowerShellWasmSession;
}

export interface PowerShellWasmScriptOptions {
  environment?: Record<string, string>;
  output?: Element | string;
  selector?: string;
  session?: boolean | string | PowerShellWasmSession;
}

export function executePowerShell(script: string, options?: PowerShellWasmOptions): Promise<string>;

export function executePowerShellResult(script: string, options?: PowerShellWasmOptions): Promise<PowerShellWasmResult>;

export function createPowerShellSession(options?: PowerShellWasmOptions): Promise<PowerShellWasmSession>;

export function executePowerShellSession(session: string | PowerShellWasmSession, script: string): Promise<string>;

export function executePowerShellSessionResult(session: string | PowerShellWasmSession, script: string): Promise<PowerShellWasmResult>;

export function disposePowerShellSession(session: string | PowerShellWasmSession): Promise<boolean>;

export function runPowerShellScripts(options?: PowerShellWasmScriptOptions): Promise<void>;

export function renderPowerShellResult(result: PowerShellWasmResult, target?: Element | string): void;

export function renderPowerShellOutput(text: string, target?: Element | string): void;
