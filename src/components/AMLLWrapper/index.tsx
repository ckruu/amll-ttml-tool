import "@applemusic-like-lyrics/core/style.css";
import { LayoutReason, setVisualTuning } from "@applemusic-like-lyrics/core";
import {
	LyricPlayer,
	type LyricPlayerRef,
} from "@applemusic-like-lyrics/react";
import { Card, Flex, Text } from "@radix-ui/themes";
import { useAtomValue, useSetAtom } from "jotai";
import { memo, useEffect, useMemo, useRef } from "react";
import { useTranslation } from "react-i18next";
import { audioEngine } from "$/modules/audio/audio-engine";
import { audioPlayingAtom } from "$/modules/audio/states";
import { usePageVisible } from "$/hooks/usePageVisible";
import {
	lyricWordFadeWidthAtom,
	showRomanLinesAtom,
	showTranslationLinesAtom,
	detectedRefreshHzAtom,
	previewAnimSpeedAtom,
	previewAutoContrastAtom,
	previewBgBrightnessAtom,
	previewBgContrastAtom,
	previewBgEnabledAtom,
	previewBgSaturationAtom,
	previewBgStaticAtom,
	previewBlurStrengthAtom,
	previewCascadeAtom,
	previewEmphasisAtom,
	previewGlowAtom,
	previewInactiveBlurAtom,
	previewInactiveScaleAtom,
	previewPlusLighterAtom,
	previewWordLiftAtom,
	suspendBackgroundPreviewsAtom,
} from "$/modules/settings/states/preview";
import { fontStacksAtom } from "$/modules/settings/states/fonts";
import { isDarkThemeAtom, lyricLinesAtom } from "$/states/main.ts";
import type { LyricLine } from "$/types/ttml";
import styles from "./index.module.css";
import { cappedTick, estimateRefreshHz } from "./frameLoop";
import { isEmptyInterludeLine } from "./interlude";
import { TrackBackground, useTrackArt } from "./TrackBackground";

export interface AMLLPreviewOptions {
	fontSizePx?: number | null;
	fpsCap?: number | null;
	suspendable?: boolean;
}

interface ResizablePlayer {
	onResize(): void;
}

export interface AMLLPlayerViewProps extends AMLLPreviewOptions {
	lines: LyricLine[];
}

export const AMLLPlayerView = memo(
	({
		lines,
		fontSizePx = null,
		fpsCap = null,
		suspendable = true,
	}: AMLLPlayerViewProps) => {
		const { t } = useTranslation();
		const suspended =
			useAtomValue(suspendBackgroundPreviewsAtom) && suspendable;
		const isPlaying = useAtomValue(audioPlayingAtom);
		const darkMode = useAtomValue(isDarkThemeAtom);
		const showTranslationLines = useAtomValue(showTranslationLinesAtom);
		const showRomanLines = useAtomValue(showRomanLinesAtom);
		const wordFadeWidth = useAtomValue(lyricWordFadeWidthAtom);
		const fontStacks = useAtomValue(fontStacksAtom);
		const setDetectedHz = useSetAtom(detectedRefreshHzAtom);
		const playerRef = useRef<LyricPlayerRef>(null);
		const pageVisible = usePageVisible();

		const emphasis = useAtomValue(previewEmphasisAtom);
		const glow = useAtomValue(previewGlowAtom);
		const wordLift = useAtomValue(previewWordLiftAtom);
		const animSpeed = useAtomValue(previewAnimSpeedAtom);
		const inactiveScale = useAtomValue(previewInactiveScaleAtom);
		const cascade = useAtomValue(previewCascadeAtom);
		const inactiveBlur = useAtomValue(previewInactiveBlurAtom);
		const blurStrength = useAtomValue(previewBlurStrengthAtom);
		const plusLighter = useAtomValue(previewPlusLighterAtom);

		const bgEnabled = useAtomValue(previewBgEnabledAtom);
		const bgStatic = useAtomValue(previewBgStaticAtom);
		const bgBrightness = useAtomValue(previewBgBrightnessAtom);
		const bgContrast = useAtomValue(previewBgContrastAtom);
		const bgSaturation = useAtomValue(previewBgSaturationAtom);
		const autoContrast = useAtomValue(previewAutoContrastAtom);
		const trackArt = useTrackArt();

		const capped = fpsCap != null && fpsCap > 0;

		const lyricLines = useMemo(() => {
			return structuredClone(
				lines
					.filter((line) => !isEmptyInterludeLine(line))
					.map((line) => ({
						...line,
						translatedLyric: showTranslationLines ? line.translatedLyric : "",
						romanLyric: showRomanLines ? line.romanLyric : "",
					})),
			);
		}, [lines, showTranslationLines, showRomanLines]);

		useEffect(() => {
			try {
				setVisualTuning({
					emphasis,
					glow,
					lift: wordLift,
					cascade,
					inactiveScale,
					blur: blurStrength,
					animSpeed,
				});
			} catch {
			}
		}, [
			emphasis,
			glow,
			wordLift,
			cascade,
			inactiveScale,
			blurStrength,
			animSpeed,
		]);

		const scaleSpringParams = useMemo(
			() => ({
				stiffness: 100 * Math.max(0.2, animSpeed),
				damping: 25 * Math.sqrt(Math.max(0.2, animSpeed)),
			}),
			[animSpeed],
		);

		const contrastLyricColor =
			autoContrast && trackArt?.paletteIsDark != null
				? trackArt.paletteIsDark
					? "#f5f5f5"
					: "#141414"
				: undefined;

		useEffect(() => {
			const updateAMLLTime = (timeInSeconds: number) => {
				if (playerRef.current?.lyricPlayer) {
					playerRef.current.lyricPlayer.setCurrentTime(timeInSeconds * 1000);
				}
			};

			updateAMLLTime(audioEngine.musicCurrentTime);
			audioEngine.onTimeUpdate(updateAMLLTime);
			return () => audioEngine.offTimeUpdate(updateAMLLTime);
		}, []);

		useEffect(() => {
			let raf = 0;
			let last = -1;
			let done = false;
			const deltas: number[] = [];
			const tick = (now: number) => {
				if (done) return;
				if (last >= 0) deltas.push(now - last);
				last = now;
				if (deltas.length >= 60) {
					done = true;
					const hz = estimateRefreshHz(deltas);
					if (hz != null) setDetectedHz(hz);
					return;
				}
				raf = requestAnimationFrame(tick);
			};
			raf = requestAnimationFrame(tick);
			return () => {
				done = true;
				cancelAnimationFrame(raf);
			};
		}, [setDetectedHz]);

		useEffect(() => {
			if (!capped || fpsCap == null || !pageVisible) return;
			const interval = 1000 / fpsCap;
			let raf = 0;
			let last = -1;
			let laidOut = false;
			let cancelled = false;
			const tick = (now: number) => {
				if (cancelled) return;
				raf = requestAnimationFrame(tick);
				const player = playerRef.current?.lyricPlayer;
				if (!player) return;
				if (!laidOut) {
					player.calcLayout(LayoutReason.ConfigChange);
					laidOut = true;
					last = now;
					return;
				}
				if (last < 0) {
					last = now;
					return;
				}
				const t = cappedTick(last, now, interval);
				last = t.nextLast;
				if (t.fire) player.update(t.delta);
			};
			raf = requestAnimationFrame(tick);
			return () => {
				cancelled = true;
				cancelAnimationFrame(raf);
			};
		}, [capped, fpsCap, pageVisible]);

		useEffect(() => {
			if (fontSizePx == null) return;
			let raf = 0;
			let tries = 0;
			const attempt = () => {
				const player = playerRef.current?.lyricPlayer as unknown as
					| ResizablePlayer
					| undefined;
				if (player && typeof player.onResize === "function") {
					player.onResize();
					return;
				}
				tries += 1;
				if (tries < 30) raf = requestAnimationFrame(attempt);
			};
			raf = requestAnimationFrame(attempt);
		return () => cancelAnimationFrame(raf);
		}, [fontSizePx]);

		if (suspended) {
			return (
				<Card
					className={`${styles.amllWrapper} ${darkMode ? styles.isDark : ""}`}
				>
					<Flex align="center" justify="center" height="100%" p="4">
						<Text size="1" color="gray" align="center">
							{t(
								"mergeDialog.backgroundPaused",
								"后台预览已暂停，关闭合并编辑器后恢复。",
							)}
						</Text>
					</Flex>
				</Card>
			);
		}

		return (
			<Card
			className={`${styles.amllWrapper} ${darkMode ? styles.isDark : ""}`}
			style={
				{
					"--default-font-family": fontStacks.preview,
					...(fontSizePx != null
						? { "--amll-lp-font-size": `${fontSizePx}px` }
						: {}),
					...(contrastLyricColor != null
						? { "--amll-lp-color": contrastLyricColor }
						: {}),
				} as import("react").CSSProperties
			}
			>
				{bgEnabled && (
					<TrackBackground
						art={trackArt}
						brightness={bgBrightness}
						contrast={bgContrast}
						saturation={bgSaturation}
						staticMode={bgStatic}
						hasLyric={lyricLines.length > 0}
					/>
				)}
				<div
					className={`${styles.playerLayer} ${plusLighter ? styles.plusLighter : ""}`}
				>
					<LyricPlayer
						style={{
							height: "100%",
							boxSizing: "content-box",
						}}
						onLyricLineClick={(evt) => {
							playerRef.current?.lyricPlayer?.resetScroll();
							audioEngine.seekMusic(evt.line.getLine().startTime / 1000);
						}}
					lyricLines={lyricLines}
					playing={isPlaying}
					disabled={capped || !pageVisible}
						wordFadeWidth={wordFadeWidth}
						enableBlur={inactiveBlur}
						lineScaleSpringParams={scaleSpringParams}
						ref={playerRef}
					/>
				</div>
			</Card>
		);
	},
);

export const AMLLWrapper = memo((props: AMLLPreviewOptions = {}) => {
	const originalLyricLines = useAtomValue(lyricLinesAtom);
	return (
		<AMLLPlayerView {...props} lines={originalLyricLines.lyricLines} />
	);
});

export default AMLLWrapper;
