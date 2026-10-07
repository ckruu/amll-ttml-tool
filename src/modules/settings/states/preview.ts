import { atom, useAtomValue } from "jotai";
import { atomWithStorage } from "jotai/utils";

export const showTranslationLinesAtom = atomWithStorage(
	"showTranslationLines",
	false,
);
export const showRomanLinesAtom = atomWithStorage("showRomanLines", false);
export const hideObsceneWordsAtom = atomWithStorage("hideObsceneWords", false);
export const lyricWordFadeWidthAtom = atomWithStorage(
	"lyricWordFadeWidth",
	0.5,
);

/**
 * Sidebar preview font size (px) set from the ribbon View section.
 */
export const sidebarPreviewFontSizeAtom = atomWithStorage(
	"settings_sidebarPreviewFontSize",
	13,
);

/**
 * In-panel sidebar preview font size override (px).
 * `null` means "follow the ribbon setting".
 */
export const sidebarPreviewPanelFontSizeAtom = atomWithStorage<
	number | null
>("settings_sidebarPreviewPanelFontSize", null);

/**
 * Effective sidebar preview font size: panel override wins when set.
 */
export const effectiveSidebarPreviewFontSizeAtom = atom(
	(get) =>
		get(sidebarPreviewPanelFontSizeAtom) ?? get(sidebarPreviewFontSizeAtom),
);

/**
 * Main Preview-view font size (px).
 */
export const previewViewFontSizeAtom = atomWithStorage(
	"settings_previewViewFontSize",
	16,
);

/**
 * Shared vsync toggle: when on, player frame loops are capped at the
 * detected display refresh rate.
 */
export const previewVsyncEnabledAtom = atomWithStorage(
	"settings_previewVsyncEnabled",
	false,
);

/**
 * Auto-detected display refresh rate (Hz). Session-only.
 */
export const detectedRefreshHzAtom = atom<number | null>(null);

/**
 * While true, background preview players (sidebar tab, main preview view)
 * unmount instead of rendering, so the merge editor modal holds the only
 * live player. Set by the merge modal on open, cleared on close.
 */
export const suspendBackgroundPreviewsAtom = atom(false);

/**
 * True-fullscreen lyrics preview (all app chrome hidden). Transient: the
 * Fullscreen API needs a user gesture, so this is never persisted.
 */
export const previewFullscreenAtom = atom(false);

/**
 * Vsync FPS cap for a preview surface: the detected display rate when
 * vsync is on, null (stock uncapped loop) when off.
 */
export const usePreviewFpsCap = (): number | null => {
	const vsync = useAtomValue(previewVsyncEnabledAtom);
	const hz = useAtomValue(detectedRefreshHzAtom);
	return vsync ? (hz ?? 60) : null;
};

//#region Preview lyric visual style (fractions; 1 = stock look)

/** Emphasis scale/bump on long-held words (0–2) */
export const previewEmphasisAtom = atomWithStorage(
	"settings_previewEmphasis",
	1,
);
/** White glow intensity on emphasized words (0–2) */
export const previewGlowAtom = atomWithStorage("settings_previewGlow", 1);
/** How far each word rises as it is sung (0–2) */
export const previewWordLiftAtom = atomWithStorage(
	"settings_previewWordLift",
	1,
);
/** Line-transition spring speed multiplier (0.2–2) */
export const previewAnimSpeedAtom = atomWithStorage(
	"settings_previewAnimSpeed",
	1,
);
/** Inactive line scale fraction (0.5–1, stock 0.97) */
export const previewInactiveScaleAtom = atomWithStorage(
	"settings_previewInactiveScale",
	0.97,
);
/** Staggered line-entry multiplier (0–2, 0 = no stagger) */
export const previewCascadeAtom = atomWithStorage(
	"settings_previewCascade",
	1,
);
/** Blur on inactive lines on/off */
export const previewInactiveBlurAtom = atomWithStorage(
	"settings_previewInactiveBlur",
	true,
);
/** Inactive blur strength multiplier (0–2) */
export const previewBlurStrengthAtom = atomWithStorage(
	"settings_previewBlurStrength",
	1,
);
/** Inactive-line glow enhancement (plus-lighter intent) */
export const previewPlusLighterAtom = atomWithStorage(
	"settings_previewPlusLighter",
	true,
);

//#endregion

//#region Preview beat-reactive background

/** Master toggle for the shader background */
export const previewBgEnabledAtom = atomWithStorage(
	"settings_previewBgEnabled",
	true,
);
/** Freeze shader motion */
export const previewBgStaticAtom = atomWithStorage(
	"settings_previewBgStatic",
	false,
);
/** Background brightness cap (0.2–1.5) */
export const previewBgBrightnessAtom = atomWithStorage(
	"settings_previewBgBrightness",
	1,
);
/** Background contrast (0.5–1.5) */
export const previewBgContrastAtom = atomWithStorage(
	"settings_previewBgContrast",
	1,
);
/** Background saturation (0–2) */
export const previewBgSaturationAtom = atomWithStorage(
	"settings_previewBgSaturation",
	1,
);
/** Adapt lyric color to the background palette automatically */
export const previewAutoContrastAtom = atomWithStorage(
	"settings_previewAutoContrast",
	true,
);

//#endregion
