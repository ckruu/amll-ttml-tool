import { Dismiss24Regular, PauseFilled, PlayFilled } from "@fluentui/react-icons";
import { Box, Flex, IconButton, Tooltip } from "@radix-ui/themes";
import { useAtomValue, useSetAtom } from "jotai";
import { lazy, useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import SuspensePlaceHolder from "$/components/SuspensePlaceHolder";
import { audioEngine } from "$/modules/audio/audio-engine";
import { audioPlayingAtom } from "$/modules/audio/states";
import {
	previewFullscreenAtom,
	previewViewFontSizeAtom,
	usePreviewFpsCap,
} from "$/modules/settings/states/preview";
import styles from "./PreviewFullscreenHost.module.css";

const AMLLWrapper = lazy(() => import("$/components/AMLLWrapper"));

/**
 * True-fullscreen lyrics preview: only the player, no app chrome.
 * Mounted by App in place of the whole chrome column while
 * {@link previewFullscreenAtom} is true. Exits via overlay button, Esc
 * (native fullscreenchange), or the ribbon toggle.
 */
export const PreviewFullscreenHost = () => {
	const { t } = useTranslation();
	const setFullscreen = useSetAtom(previewFullscreenAtom);
	const previewViewFontSize = useAtomValue(previewViewFontSizeAtom);
	const previewFpsCap = usePreviewFpsCap();
	const audioPlaying = useAtomValue(audioPlayingAtom);
	const containerRef = useRef<HTMLDivElement>(null);

	// Request OS-level fullscreen. Must run in (transient) user-activation
	// context; if the browser rejects, the chromeless view still applies.
	useEffect(() => {
		const el = containerRef.current;
		if (!el) return;
		if (document.fullscreenElement) return;
		try {
			const result = el.requestFullscreen() as unknown as
				| Promise<void>
				| undefined;
			result?.catch?.(() => {});
		} catch {
			// stay chromeless without OS fullscreen
		}
	}, []);

	// Native exit (Esc etc.) syncs the atom back.
	useEffect(() => {
		const onChange = () => {
			if (!document.fullscreenElement) setFullscreen(false);
		};
		document.addEventListener("fullscreenchange", onChange);
		return () => {
			document.removeEventListener("fullscreenchange", onChange);
		};
	}, [setFullscreen]);

	const exitFullscreen = () => {
		if (document.fullscreenElement) {
			document.exitFullscreen().catch(() => {});
		}
		setFullscreen(false);
	};

	return (
		<div ref={containerRef} className={styles.host}>
			<SuspensePlaceHolder>
				<AMLLWrapper
					fontSizePx={previewViewFontSize}
					fpsCap={previewFpsCap}
				/>
			</SuspensePlaceHolder>
			<Box className={styles.overlay}>
				<Flex gap="2" align="center" justify="center" p="2">
					<Tooltip
						content={
							audioPlaying
								? t("previewFullscreen.pause", "暂停")
								: t("previewFullscreen.play", "播放")
						}
					>
						<IconButton
							size="2"
							variant="soft"
							onClick={() => {
								if (audioEngine.musicPlaying) {
									audioEngine.pauseMusic();
								} else {
									audioEngine.resumeMusic();
								}
							}}
							aria-label={
								audioPlaying
									? t("previewFullscreen.pause", "暂停")
									: t("previewFullscreen.play", "播放")
							}
						>
							{audioPlaying ? <PauseFilled /> : <PlayFilled />}
						</IconButton>
					</Tooltip>
					<Tooltip content={t("previewFullscreen.exit", "退出全屏")}>
						<IconButton
							size="2"
							variant="soft"
							color="gray"
							onClick={exitFullscreen}
							aria-label={t("previewFullscreen.exit", "退出全屏")}
						>
							<Dismiss24Regular />
						</IconButton>
					</Tooltip>
				</Flex>
			</Box>
		</div>
	);
};
