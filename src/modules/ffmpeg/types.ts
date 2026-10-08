export interface QueueConfig {
	capacitySeconds: number;
	notifyWatermarkSeconds: number;
	emergencyWatermarkSeconds: number;
}

export interface EngineConfig {
	audioContext: AudioContext;

	gainNode?: GainNode;

	assets: {
		workerUrl: string;
		workletUrl: string;
		ffmpegWasmUrl: string;
		soundtouchWasmUrl: string;
	};

	queueConfig?: Partial<QueueConfig>;

	defaultAlgorithm?: StretchAlgorithm;
}

export type StretchAlgorithm = "wsola" | "spectral";

export type EngineState = "idle" | "loading" | "ready" | "playing" | "paused";

export const EngineErrorCode = {
	Aborted: 1,
	Network: 2,
	Decode: 3,
	SrcNotSupported: 4,
} as const;

export type EngineErrorCodeValue =
	(typeof EngineErrorCode)[keyof typeof EngineErrorCode];

export interface EngineError {
	code: EngineErrorCodeValue;
	message: string;
}

export interface PlayerCover {
	bytes: ArrayBuffer;
	mime: string | null;
}

export interface EngineEventMap {
	play: CustomEvent<void>;
	pause: CustomEvent<void>;
	loadedmetadata: CustomEvent<void>;
	timeupdate: CustomEvent<void>;
	ended: CustomEvent<void>;
	error: CustomEvent<EngineError>;
}
