import {
	BackgroundRender,
	type BackgroundRenderRef,
} from "@applemusic-like-lyrics/react";
import { createPaletteFromImage } from "@applemusic-like-lyrics/core";
import { useAtomValue } from "jotai";
import { memo, useEffect, useRef, useState } from "react";
import { usePageVisible } from "$/hooks/usePageVisible";
import { audioEngine } from "$/modules/audio/audio-engine";
import { audioPlayingAtom, coverNonceAtom, loadedAudioAtom } from "$/modules/audio/states";

export interface TrackArt {
	url: string;
	img: HTMLImageElement;
	paletteIsDark: boolean | null;
}

const artCache = new Map<
	Blob,
	{ art: TrackArt; refs: number }
>();

function retainArt(blob: Blob, art: TrackArt) {
	const entry = artCache.get(blob);
	if (entry) {
		entry.refs += 1;
		return;
	}
	artCache.set(blob, { art, refs: 1 });
}

function releaseArt(blob: Blob) {
	const entry = artCache.get(blob);
	if (!entry) return;
	entry.refs -= 1;
	if (entry.refs <= 0) {
		artCache.delete(blob);
		URL.revokeObjectURL(entry.art.url);
	}
}

/**
 * Track cover art + palette, shared across preview instances via a
 * blob-keyed cache (object URL + palette computed once per track).
 */
export function useTrackArt(): TrackArt | null {
	const loadedAudio = useAtomValue(loadedAudioAtom);
	// Bumped after each decode completes, when cover bytes are final.
	// (loadedAudio alone fires before decode, when cover is still stale.)
	const coverNonce = useAtomValue(coverNonceAtom);
	const [art, setArt] = useState<TrackArt | null>(null);

	useEffect(() => {
		// No decode has completed yet (or state reset): nothing to read.
		if (coverNonce <= 0) {
			setArt(null);
			return;
		}
		let cancelled = false;
		const cached = artCache.get(loadedAudio);
		if (cached) {
			cached.refs += 1;
			setArt(cached.art);
			return () => {
				releaseArt(loadedAudio);
			};
		}
		(async () => {
			try {
				const cover = audioEngine.cover;
				const bytes = cover?.bytes;
				if (!bytes || bytes.byteLength === 0) {
					if (!cancelled) setArt(null);
					return;
				}
				const blob = new Blob([bytes], {
					type: cover.mime || "image/png",
				});
				const url = URL.createObjectURL(blob);
				const img = new Image();
				img.src = url;
				await img.decode();
				if (cancelled) {
					URL.revokeObjectURL(url);
					return;
				}
				let paletteIsDark: boolean | null = null;
				try {
					const palette = createPaletteFromImage(img, 6, {
						intent: "dominant",
					});
					paletteIsDark = palette.paletteIsDark;
				} catch {
					paletteIsDark = null;
				}
				if (cancelled) {
					URL.revokeObjectURL(url);
					return;
				}
				const next: TrackArt = { url, img, paletteIsDark };
				retainArt(loadedAudio, next);
				setArt(next);
			} catch {
				if (!cancelled) setArt(null);
			}
		})();
		return () => {
			cancelled = true;
			releaseArt(loadedAudio);
		};
	}, [loadedAudio, coverNonce]);

	return art;
}

/**
 * Beat-reactive shader background behind the lyrics. Cover art (when the
 * loaded track embeds any) drives the mesh gradient; low-frequency energy
 * from the master gain drives the pulse. No audio file → default gradient.
 */
export const TrackBackground = memo(
	({
		art,
		brightness,
		contrast,
		saturation,
		staticMode,
		hasLyric,
	}: {
		art: TrackArt | null;
		brightness: number;
		contrast: number;
		saturation: number;
		staticMode: boolean;
		hasLyric: boolean;
	}) => {
		const isPlaying = useAtomValue(audioPlayingAtom);
		const bgRenderRef = useRef<BackgroundRenderRef>(null);
		const pageVisible = usePageVisible();
		const pageVisibleRef = useRef(pageVisible);
		pageVisibleRef.current = pageVisible;

		useEffect(() => {
			let raf = 0;
			let last = 0;
			let cancelled = false;
			const analyser = audioEngine.getBeatAnalyser();
			const data =
				analyser != null
					? new Uint8Array(analyser.frequencyBinCount)
					: null;
			const tick = (now: number) => {
				if (cancelled) return;
				raf = requestAnimationFrame(tick);
				if (now - last < 100) return;
				last = now;
				if (!pageVisibleRef.current) return;
				const inst = bgRenderRef.current?.bgRender;
				if (!inst || !analyser || !data) return;
				if (!audioEngine.musicPlaying) {
					inst.setLowFreqVolume(0);
					return;
				}
				analyser.getByteFrequencyData(data);
				let sampleRate = 48000;
				try {
					sampleRate = audioEngine.ctx.sampleRate;
				} catch {
					// keep fallback
				}
				const binHz = sampleRate / analyser.fftSize;
				const lo = Math.max(0, Math.floor(80 / binHz));
				const hi = Math.min(data.length - 1, Math.ceil(120 / binHz));
				let sum = 0;
				let n = 0;
				for (let i = lo; i <= hi; i++) {
					sum += data[i] / 255;
					n += 1;
				}
				inst.setLowFreqVolume(n > 0 ? Math.min(1, sum / n) : 0);
			};
			raf = requestAnimationFrame(tick);
			return () => {
				cancelled = true;
				cancelAnimationFrame(raf);
			};
		}, []);

		return (
			<div
				style={{
					position: "absolute",
					inset: 0,
					overflow: "hidden",
					pointerEvents: "none",
					filter: `brightness(${brightness}) contrast(${contrast}) saturate(${saturation})`,
				}}
			>
				<BackgroundRender
					ref={bgRenderRef}
					album={art?.img}
					playing={isPlaying && !staticMode}
					staticMode={staticMode}
					hasLyric={hasLyric}
				/>
			</div>
		);
	},
);
