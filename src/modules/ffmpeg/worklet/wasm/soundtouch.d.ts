
export class SoundTouchProcessor {
    free(): void;
    [Symbol.dispose](): void;
    clear(): void;
    extractOutput(max_samples: number): number;
    getAlgorithm(): StretchAlgorithm;
    getInputChunkSize(): number;
    getInputPtr(channel: number): number;
    getOutputPtr(channel: number): number;
    constructor(channels: number, sample_rate: number, algorithm?: StretchAlgorithm | null);
    numSamples(): number;
    processInput(num_samples: number): void;
    setAlgorithm(algorithm: StretchAlgorithm): void;
    setFormantFactor(factor: number, compensate_pitch: boolean): void;
    setPitch(pitch: number): void;
    setRate(rate: number): void;
    setTempo(tempo: number): void;
}

export enum StretchAlgorithm {
    Wsola = 0,
    Spectral = 1,
}

export type InitInput = RequestInfo | URL | Response | BufferSource | WebAssembly.Module;

export interface InitOutput {
    readonly memory: WebAssembly.Memory;
    readonly __wbg_soundtouchprocessor_free: (a: number, b: number) => void;
    readonly soundtouchprocessor_clear: (a: number) => void;
    readonly soundtouchprocessor_extractOutput: (a: number, b: number, c: number) => void;
    readonly soundtouchprocessor_getAlgorithm: (a: number) => number;
    readonly soundtouchprocessor_getInputChunkSize: (a: number) => number;
    readonly soundtouchprocessor_getInputPtr: (a: number, b: number) => number;
    readonly soundtouchprocessor_getOutputPtr: (a: number, b: number) => number;
    readonly soundtouchprocessor_new: (a: number, b: number, c: number, d: number) => void;
    readonly soundtouchprocessor_numSamples: (a: number) => number;
    readonly soundtouchprocessor_processInput: (a: number, b: number, c: number) => void;
    readonly soundtouchprocessor_setAlgorithm: (a: number, b: number, c: number) => void;
    readonly soundtouchprocessor_setFormantFactor: (a: number, b: number, c: number) => void;
    readonly soundtouchprocessor_setPitch: (a: number, b: number) => void;
    readonly soundtouchprocessor_setRate: (a: number, b: number) => void;
    readonly soundtouchprocessor_setTempo: (a: number, b: number) => void;
    readonly __wbindgen_add_to_stack_pointer: (a: number) => number;
}

export type SyncInitInput = BufferSource | WebAssembly.Module;

export function initSync(module: { module: SyncInitInput } | SyncInitInput): InitOutput;

export default function __wbg_init (module_or_path?: { module_or_path: InitInput | Promise<InitInput> } | InitInput | Promise<InitInput>): Promise<InitOutput>;
