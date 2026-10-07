import { atom } from "jotai/index";
import { atomWithStorage } from "jotai/utils";
import type { EngineState, StretchAlgorithm } from "$/modules/ffmpeg/types.ts";
import type { BpmAnalysisResult } from "$/modules/ffmpeg/worker/wasm/bpm-analyzer/bpm_analyzer_wasm";

export type BpmState =
	| { status: "idle" }
	| { status: "analyzing" }
	| {
			status: "completed";
			result: BpmAnalysisResult;
			calculationTime: number;
	  }
	| {
			status: "error";
			error: string;
	  };

export const bpmStateAtom = atom<BpmState>({ status: "idle" });
export const bpmScaleAtom = atom<number>(1);
export const bpmFollowPlaybackRateAtom = atomWithStorage(
	"bpmFollowPlaybackRate",
	true,
);

export const audioEngineStateAtom = atom<EngineState>("idle");
export const volumeAtom = atomWithStorage("volume", 0.5);
export const playbackRateAtom = atomWithStorage("playbackRate", 1);
export const stretchAlgorithmAtom = atomWithStorage<StretchAlgorithm>(
	"stretchAlgorithm",
	"spectral",
);
export const audioPlayingAtom = atom(false);
export const loadedAudioAtom = atom(new Blob([]));
/**
 * Bumped every time a track finishes decoding (cover art final — present
 * or definitively absent). Cover bytes themselves are read imperatively
 * from the engine; this is only the reactive "ready" signal.
 */
export const coverNonceAtom = atom(0);
export const currentDurationAtom = atom(0);
export const isAuditioningAtom = atom(false);
export const audioErrorAtom = atom<string | null>(null);
export const pcmDataReadyAtom = atom(false);

export type BpmTapMode = "off" | "key" | "spectrogram";
export const bpmTapModeAtom = atom<BpmTapMode>("off");
export const tapTimesAtom = atom<number[]>([]);
export const totalTapCountAtom = atom<number>(0);
export const hasSeenTapWindowTipAtom = atomWithStorage(
	"hasSeenTapWindowTip",
	false,
);
