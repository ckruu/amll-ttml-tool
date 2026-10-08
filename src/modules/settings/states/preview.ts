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

export const sidebarPreviewFontSizeAtom = atomWithStorage(
	"settings_sidebarPreviewFontSize",
	13,
);

export const sidebarPreviewPanelFontSizeAtom = atomWithStorage<
	number | null
>("settings_sidebarPreviewPanelFontSize", null);

export const effectiveSidebarPreviewFontSizeAtom = atom(
	(get) =>
		get(sidebarPreviewPanelFontSizeAtom) ?? get(sidebarPreviewFontSizeAtom),
);

export const previewViewFontSizeAtom = atomWithStorage(
	"settings_previewViewFontSize",
	16,
);

export const previewVsyncEnabledAtom = atomWithStorage(
	"settings_previewVsyncEnabled",
	false,
);

export const detectedRefreshHzAtom = atom<number | null>(null);

export const suspendBackgroundPreviewsAtom = atom(false);

export const previewFullscreenAtom = atom(false);

export const usePreviewFpsCap = (): number | null => {
	const vsync = useAtomValue(previewVsyncEnabledAtom);
	const hz = useAtomValue(detectedRefreshHzAtom);
	return vsync ? (hz ?? 60) : null;
};

export const previewEmphasisAtom = atomWithStorage(
	"settings_previewEmphasis",
	1,
);
export const previewGlowAtom = atomWithStorage("settings_previewGlow", 1);
export const previewWordLiftAtom = atomWithStorage(
	"settings_previewWordLift",
	1,
);
export const previewAnimSpeedAtom = atomWithStorage(
	"settings_previewAnimSpeed",
	1,
);
export const previewInactiveScaleAtom = atomWithStorage(
	"settings_previewInactiveScale",
	0.97,
);
export const previewCascadeAtom = atomWithStorage(
	"settings_previewCascade",
	1,
);
export const previewInactiveBlurAtom = atomWithStorage(
	"settings_previewInactiveBlur",
	true,
);
export const previewBlurStrengthAtom = atomWithStorage(
	"settings_previewBlurStrength",
	1,
);
export const previewPlusLighterAtom = atomWithStorage(
	"settings_previewPlusLighter",
	true,
);

export const previewBgEnabledAtom = atomWithStorage(
	"settings_previewBgEnabled",
	true,
);
export const previewBgStaticAtom = atomWithStorage(
	"settings_previewBgStatic",
	false,
);
export const previewBgBrightnessAtom = atomWithStorage(
	"settings_previewBgBrightness",
	1,
);
export const previewBgContrastAtom = atomWithStorage(
	"settings_previewBgContrast",
	1,
);
export const previewBgSaturationAtom = atomWithStorage(
	"settings_previewBgSaturation",
	1,
);
export const previewAutoContrastAtom = atomWithStorage(
	"settings_previewAutoContrast",
	true,
);
