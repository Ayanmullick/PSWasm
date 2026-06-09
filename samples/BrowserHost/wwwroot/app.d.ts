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

export interface PowerShellWasmOptions {
  environment?: Record<string, string>;
  output?: Element | string;
  selector?: string;
}

export function executePowerShell(script: string, options?: PowerShellWasmOptions): Promise<string>;

export function executePowerShellResult(script: string, options?: PowerShellWasmOptions): Promise<PowerShellWasmResult>;

export function runPowerShellScripts(options?: PowerShellWasmOptions): Promise<void>;

export function renderPowerShellResult(result: PowerShellWasmResult, target?: Element | string): void;

export function renderPowerShellOutput(text: string, target?: Element | string): void;
