import type { QueueConfig } from "../types";

const CONTROL_BLOCK_BYTES = 128;

const CONFIG_CHANNELS = 0;
const CONFIG_CAPACITY_FRAMES = 1;
const CONFIG_NOTIFY_FRAMES = 2;
const CONFIG_EMERGENCY_FRAMES = 3;

const STATE_PLAYING = 16;
const STATE_IS_SEEKING = 17;
const STATE_READ_INDEX = 18;
const STATE_WRITE_INDEX = 19;
const STATE_PLAYBACK_INDEX = 20;
const STATE_PAUSE_AT_INDEX = 21;
const STATE_SEEK_GENERATION = 22;
const STATE_UNNOTIFIED_FRAMES = 23;

export interface MainAudioController {
	play(): void;
	pause(): void;
	getReadIndex(): number;
	getPlaybackIndex(): number;
	setSeeking(isSeeking: boolean): void;
	setPauseAtIndex(index: number): void;
	clearPauseAtIndex(): void;
}

export interface AudioWriter {
	getAvailableWriteSpace(): number;

	writePartial(
		channelDatas: Float32Array[],
		offset: number,
		length: number,
	): number;

	beginSeek(): void;

	endSeek(): void;

	waitForSpaceAsync(minSpaceReq: number): {
		async: boolean;
		promise?: Promise<void>;
	};

	waitForDrainAsync(): { isDrained: boolean; promise?: Promise<void> };
}

export interface AudioReader {
	isPlaying(): boolean;
	isSeeking(): boolean;
	getAvailableReadFrames(): number;
	readPartial(outputs: Float32Array[], length: number): number;
	addPlaybackIndex(frames: number): void;
	getPlaybackIndex(): number;
	rewindReadIndexToPlayback(): void;
	getPauseAtIndex(): number;
	clearPauseAtIndex(): void;
	pausePlayback(): void;
	getSeekGeneration(): number;
}

class AudioQueueCore implements MainAudioController, AudioWriter, AudioReader {
	private controlBlock: Int32Array;
	private channelBuffers: Float32Array[] = [];
	private channels: number;
	private capacityFrames: number;

	constructor(sab: SharedArrayBuffer) {
		this.controlBlock = new Int32Array(sab, 0, CONTROL_BLOCK_BYTES / 4);

		this.channels = Atomics.load(this.controlBlock, CONFIG_CHANNELS);
		this.capacityFrames = Atomics.load(
			this.controlBlock,
			CONFIG_CAPACITY_FRAMES,
		);

		const bytesPerChannel = this.capacityFrames * 4;
		for (let c = 0; c < this.channels; c++) {
			const offset = CONTROL_BLOCK_BYTES + c * bytesPerChannel;
			this.channelBuffers.push(
				new Float32Array(sab, offset, this.capacityFrames),
			);
		}
	}

	play(): void {
		Atomics.store(this.controlBlock, STATE_PLAYING, 1);
	}

	pause(): void {
		Atomics.store(this.controlBlock, STATE_PLAYING, 0);
	}

	getReadIndex(): number {
		return Atomics.load(this.controlBlock, STATE_READ_INDEX);
	}

	getPlaybackIndex(): number {
		return Atomics.load(this.controlBlock, STATE_PLAYBACK_INDEX);
	}

	setSeeking(isSeeking: boolean): void {
		Atomics.store(this.controlBlock, STATE_IS_SEEKING, isSeeking ? 1 : 0);
	}

	setPauseAtIndex(index: number): void {
		Atomics.store(this.controlBlock, STATE_PAUSE_AT_INDEX, index);
	}

	clearPauseAtIndex(): void {
		Atomics.store(this.controlBlock, STATE_PAUSE_AT_INDEX, -1);
	}

	getAvailableWriteSpace(): number {
		const writeIndex = Atomics.load(this.controlBlock, STATE_WRITE_INDEX);
		const readIndex = Atomics.load(this.controlBlock, STATE_READ_INDEX);
		return this.capacityFrames - (writeIndex - readIndex);
	}

	writePartial(
		channelDatas: Float32Array[],
		offset: number,
		length: number,
	): number {
		const writeIndex = Atomics.load(this.controlBlock, STATE_WRITE_INDEX);
		const readIndex = Atomics.load(this.controlBlock, STATE_READ_INDEX);

		const availableSpace = this.capacityFrames - (writeIndex - readIndex);
		const writeAmount = Math.min(length, availableSpace);

		if (writeAmount === 0) {
			return 0;
		}

		const ringPos = writeIndex % this.capacityFrames;
		const spaceToEnd = this.capacityFrames - ringPos;

		for (let c = 0; c < this.channels; c++) {
			const source = channelDatas[c];
			const target = this.channelBuffers[c];

			if (writeAmount <= spaceToEnd) {
				target.set(source.subarray(offset, offset + writeAmount), ringPos);
			} else {
				target.set(source.subarray(offset, offset + spaceToEnd), ringPos);
				target.set(
					source.subarray(offset + spaceToEnd, offset + writeAmount),
					0,
				);
			}
		}

		Atomics.add(this.controlBlock, STATE_WRITE_INDEX, writeAmount);
		return writeAmount;
	}

	beginSeek(): void {
		Atomics.store(this.controlBlock, STATE_IS_SEEKING, 1);
		Atomics.store(this.controlBlock, STATE_WRITE_INDEX, 0);
		Atomics.store(this.controlBlock, STATE_READ_INDEX, 0);
		Atomics.store(this.controlBlock, STATE_PLAYBACK_INDEX, 0);
		Atomics.store(this.controlBlock, STATE_UNNOTIFIED_FRAMES, 0);
		Atomics.add(this.controlBlock, STATE_SEEK_GENERATION, 1);
		Atomics.notify(this.controlBlock, STATE_READ_INDEX, 1);
	}

	endSeek(): void {
		Atomics.store(this.controlBlock, STATE_IS_SEEKING, 0);
	}

	waitForSpaceAsync(minSpaceReq: number): {
		async: boolean;
		promise?: Promise<void>;
	} {
		const writeIndex = Atomics.load(this.controlBlock, STATE_WRITE_INDEX);
		const readIndex = Atomics.load(this.controlBlock, STATE_READ_INDEX);

		const availableSpace = this.capacityFrames - (writeIndex - readIndex);

		if (availableSpace >= minSpaceReq) {
			return { async: false };
		}

		const currentReadIndex = readIndex;
		const waitResult = Atomics.waitAsync(
			this.controlBlock,
			STATE_READ_INDEX,
			currentReadIndex,
		);

		if (waitResult.async) {
			const voidPromise = (waitResult.value as Promise<string>).then(() => {});
			return { async: true, promise: voidPromise };
		}

		return { async: false };
	}

	waitForDrainAsync(): { isDrained: boolean; promise?: Promise<void> } {
		const writeIndex = Atomics.load(this.controlBlock, STATE_WRITE_INDEX);
		const readIndex = Atomics.load(this.controlBlock, STATE_READ_INDEX);

		if (readIndex >= writeIndex) {
			return { isDrained: true };
		}

		const waitResult = Atomics.waitAsync(
			this.controlBlock,
			STATE_READ_INDEX,
			readIndex,
		);

		if (waitResult.async) {
			const voidPromise = (waitResult.value as Promise<string>).then(() => {});
			return { isDrained: false, promise: voidPromise };
		}

		return { isDrained: false };
	}

	getSeekGeneration(): number {
		return Atomics.load(this.controlBlock, STATE_SEEK_GENERATION);
	}

	isPlaying(): boolean {
		return Atomics.load(this.controlBlock, STATE_PLAYING) === 1;
	}

	isSeeking(): boolean {
		return Atomics.load(this.controlBlock, STATE_IS_SEEKING) === 1;
	}

	getAvailableReadFrames(): number {
		const writeIndex = Atomics.load(this.controlBlock, STATE_WRITE_INDEX);
		const readIndex = Atomics.load(this.controlBlock, STATE_READ_INDEX);
		return writeIndex - readIndex;
	}

	readPartial(outputs: Float32Array[], length: number): number {
		if (!this.isPlaying() || this.isSeeking()) {
			return 0;
		}

		const writeIndex = Atomics.load(this.controlBlock, STATE_WRITE_INDEX);
		const readIndex = Atomics.load(this.controlBlock, STATE_READ_INDEX);
		const availableData = writeIndex - readIndex;
		const readAmount = Math.min(length, availableData);

		if (readAmount === 0) {
			return 0;
		}

		const ringPos = readIndex % this.capacityFrames;
		const spaceToEnd = this.capacityFrames - ringPos;
		for (let c = 0; c < this.channels; c++) {
			if (!outputs[c]) continue;
			if (readAmount <= spaceToEnd) {
				outputs[c].set(
					this.channelBuffers[c].subarray(ringPos, ringPos + readAmount),
				);
			} else {
				outputs[c].set(
					this.channelBuffers[c].subarray(ringPos, this.capacityFrames),
					0,
				);
				const remaining = readAmount - spaceToEnd;
				outputs[c].set(
					this.channelBuffers[c].subarray(0, remaining),
					spaceToEnd,
				);
			}
		}

		Atomics.add(this.controlBlock, STATE_READ_INDEX, readAmount);
		Atomics.add(this.controlBlock, STATE_UNNOTIFIED_FRAMES, readAmount);
		this.notifyProducerIfNeeded(availableData - readAmount);
		return readAmount;
	}

	addPlaybackIndex(frames: number): void {
		Atomics.add(this.controlBlock, STATE_PLAYBACK_INDEX, frames);
	}

	rewindReadIndexToPlayback(): void {
		const playbackIndex = Atomics.load(this.controlBlock, STATE_PLAYBACK_INDEX);
		const writeIndex = Atomics.load(this.controlBlock, STATE_WRITE_INDEX);
		const targetIndex = Math.min(writeIndex, Math.max(0, playbackIndex));
		Atomics.store(this.controlBlock, STATE_READ_INDEX, targetIndex);
	}

	getPauseAtIndex(): number {
		return Atomics.load(this.controlBlock, STATE_PAUSE_AT_INDEX);
	}

	pausePlayback(): void {
		Atomics.store(this.controlBlock, STATE_PLAYING, 0);
	}

	private notifyProducerIfNeeded(availableData: number): void {
		const unnotified = Atomics.load(this.controlBlock, STATE_UNNOTIFIED_FRAMES);

		const notifyFrames = Atomics.load(this.controlBlock, CONFIG_NOTIFY_FRAMES);
		const emergencyFrames = Atomics.load(
			this.controlBlock,
			CONFIG_EMERGENCY_FRAMES,
		);

		if (unnotified >= notifyFrames || availableData < emergencyFrames) {
			Atomics.store(this.controlBlock, STATE_UNNOTIFIED_FRAMES, 0);
			Atomics.notify(this.controlBlock, STATE_READ_INDEX, 1);
		}
	}
}

export function allocateAudioQueueMemory(
	sampleRate: number,
	channels: number,
	config: Required<QueueConfig>,
): SharedArrayBuffer {
	const capacityFrames =
		Math.ceil((config.capacitySeconds * sampleRate) / 128) * 128;
	const notifyFrames = Math.round(config.notifyWatermarkSeconds * sampleRate);
	const emergencyFrames = Math.round(
		config.emergencyWatermarkSeconds * sampleRate,
	);

	const bytesPerChannel = capacityFrames * 4;
	const sabBytes = CONTROL_BLOCK_BYTES + bytesPerChannel * channels;
	const sab = new SharedArrayBuffer(sabBytes);

	const controlBlock = new Int32Array(sab, 0, CONTROL_BLOCK_BYTES / 4);

	Atomics.store(controlBlock, CONFIG_CHANNELS, channels);
	Atomics.store(controlBlock, CONFIG_CAPACITY_FRAMES, capacityFrames);
	Atomics.store(controlBlock, CONFIG_NOTIFY_FRAMES, notifyFrames);
	Atomics.store(controlBlock, CONFIG_EMERGENCY_FRAMES, emergencyFrames);

	Atomics.store(controlBlock, STATE_PAUSE_AT_INDEX, -1);
	Atomics.store(controlBlock, STATE_UNNOTIFIED_FRAMES, 0);

	return sab;
}

export function createMainController(
	sab: SharedArrayBuffer,
): MainAudioController {
	return new AudioQueueCore(sab);
}

export function createAudioWriter(sab: SharedArrayBuffer): AudioWriter {
	return new AudioQueueCore(sab);
}

export function createAudioReader(sab: SharedArrayBuffer): AudioReader {
	return new AudioQueueCore(sab);
}
