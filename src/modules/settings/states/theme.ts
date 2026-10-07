import { atom } from "jotai";
import { atomWithStorage } from "jotai/utils";
import { uid } from "uid";
import { isDarkThemeAtom } from "$/states/main";

export const RADIX_ACCENT_COLORS = [
	"gray",
	"gold",
	"bronze",
	"brown",
	"yellow",
	"amber",
	"orange",
	"tomato",
	"red",
	"ruby",
	"crimson",
	"pink",
	"plum",
	"purple",
	"violet",
	"iris",
	"indigo",
	"blue",
	"cyan",
	"teal",
	"jade",
	"green",
	"grass",
	"lime",
	"mint",
	"sky",
] as const;

export type RadixAccentColor = (typeof RADIX_ACCENT_COLORS)[number];

export interface ThemeScheme {
	id: string;
	name: string;
	/** Radix preset for light appearance */
	light: RadixAccentColor;
	/** Radix preset for dark appearance */
	dark: RadixAccentColor;
	/** Optional exact hex override for light appearance (e.g. "#22c55e") */
	customLight?: string;
	/** Optional exact hex override for dark appearance */
	customDark?: string;
	useCustomLight: boolean;
	useCustomDark: boolean;
	/** Active-word highlight color (solid hex; translucency applied in CSS) */
	highlightLight: string;
	highlightDark: string;
	/** Timing chip colors, shared across appearances (start vs end stay distinct) */
	timingStart: string;
	timingEnd: string;
}

/** Legacy fallbacks for schemes stored before these fields existed */
export const LEGACY_HIGHLIGHT_HEX = "#2f6fde";
export const LEGACY_TIMING_START_HEX = "#46a758";
export const LEGACY_TIMING_END_HEX = "#e5484d";

const defaultSchemes = (): ThemeScheme[] => [
	{
		id: "default-green",
		name: "Default Green",
		light: "green",
		dark: "jade",
		useCustomLight: false,
		useCustomDark: false,
		highlightLight: "#30a46c",
		highlightDark: "#29a383",
		timingStart: "#30a46c",
		timingEnd: "#e5484d",
	},
	{
		id: "ocean",
		name: "Ocean",
		light: "blue",
		dark: "sky",
		useCustomLight: false,
		useCustomDark: false,
		highlightLight: "#0090ff",
		highlightDark: "#01a7c2",
		timingStart: "#0090ff",
		timingEnd: "#e5484d",
	},
	{
		id: "sunset",
		name: "Sunset",
		light: "orange",
		dark: "tomato",
		useCustomLight: false,
		useCustomDark: false,
		highlightLight: "#f76b15",
		highlightDark: "#e54d2e",
		timingStart: "#f76b15",
		timingEnd: "#3e63dd",
	},
	{
		id: "grape",
		name: "Grape",
		light: "violet",
		dark: "purple",
		useCustomLight: false,
		useCustomDark: false,
		highlightLight: "#8e4ec6",
		highlightDark: "#a06ee8",
		timingStart: "#8e4ec6",
		timingEnd: "#46a758",
	},
];

export const themeSchemesAtom = atomWithStorage<ThemeScheme[]>(
	"settings_themeSchemes",
	defaultSchemes(),
);

export const activeThemeSchemeIdAtom = atomWithStorage<string>(
	"settings_activeThemeSchemeId",
	"default-green",
);

export const activeThemeSchemeAtom = atom((get): ThemeScheme => {
	const schemes = get(themeSchemesAtom);
	const activeId = get(activeThemeSchemeIdAtom);
	return (
		schemes.find((s) => s.id === activeId) ??
		schemes[0] ?? {
			id: "fallback",
			name: "Fallback",
			light: "green",
			dark: "jade",
			useCustomLight: false,
			useCustomDark: false,
			highlightLight: LEGACY_HIGHLIGHT_HEX,
			highlightDark: LEGACY_HIGHLIGHT_HEX,
			timingStart: LEGACY_TIMING_START_HEX,
			timingEnd: LEGACY_TIMING_END_HEX,
		}
	);
});

export interface ResolvedThemeAccent {
	/** Radix preset to pass to <Theme accentColor> (always valid) */
	accent: RadixAccentColor;
	/** Exact hex to overlay via CSS vars, if the scheme uses custom color */
	customHex?: string;
}

const HEX_RE = /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/;

export const isValidHex = (v?: string): v is string =>
	typeof v === "string" && HEX_RE.test(v.trim());

function hexToHue(hex: string): number {
	let h = hex.trim().replace("#", "");
	if (h.length === 3) h = h
		.split("")
		.map((c) => c + c)
		.join("");
	const r = Number.parseInt(h.slice(0, 2), 16) / 255;
	const g = Number.parseInt(h.slice(2, 4), 16) / 255;
	const b = Number.parseInt(h.slice(4, 6), 16) / 255;
	const max = Math.max(r, g, b);
	const min = Math.min(r, g, b);
	if (max === min) return -1; // achromatic
	const d = max - min;
	let hue = 0;
	if (max === r) hue = ((g - b) / d) % 6;
	else if (max === g) hue = (b - r) / d + 2;
	else hue = (r - g) / d + 4;
	hue *= 60;
	if (hue < 0) hue += 360;
	return hue;
}

/** Map an arbitrary hex to the closest Radix accent so <Theme> stays valid. */
export function closestAccentForHex(hex: string): RadixAccentColor {
	const hue = hexToHue(hex);
	if (hue < 0) return "gray";
	// Rough hue wheel mapping onto Radix accents
	if (hue < 15 || hue >= 345) return "red";
	if (hue < 30) return "tomato";
	if (hue < 45) return "orange";
	if (hue < 60) return "amber";
	if (hue < 75) return "yellow";
	if (hue < 95) return "lime";
	if (hue < 140) return "green";
	if (hue < 160) return "jade";
	if (hue < 175) return "teal";
	if (hue < 195) return "cyan";
	if (hue < 215) return "sky";
	if (hue < 240) return "blue";
	if (hue < 260) return "indigo";
	if (hue < 275) return "iris";
	if (hue < 290) return "violet";
	if (hue < 305) return "purple";
	if (hue < 320) return "plum";
	if (hue < 335) return "pink";
	return "crimson";
}

export function resolveThemeAccent(
	scheme: ThemeScheme,
	isDark: boolean,
): ResolvedThemeAccent {
	if (isDark) {
		if (scheme.useCustomDark && isValidHex(scheme.customDark)) {
			return {
				accent: closestAccentForHex(scheme.customDark as string),
				customHex: (scheme.customDark as string).trim(),
			};
		}
		return { accent: scheme.dark };
	}
	if (scheme.useCustomLight && isValidHex(scheme.customLight)) {
		return {
			accent: closestAccentForHex(scheme.customLight as string),
			customHex: (scheme.customLight as string).trim(),
		};
	}
	return { accent: scheme.light };
}

export const resolvedThemeAccentAtom = atom((get): ResolvedThemeAccent => {
	const scheme = get(activeThemeSchemeAtom);
	const isDark = get(isDarkThemeAtom);
	return resolveThemeAccent(scheme, isDark);
});

export const normalizeHex = (v: unknown, fallback: string): string =>
	isValidHex(typeof v === "string" ? v : undefined)
		? (v as string).trim()
		: fallback;

/** Active-word highlight hex for the current appearance */
export const resolvedHighlightAtom = atom((get): string => {
	const scheme = get(activeThemeSchemeAtom);
	const isDark = get(isDarkThemeAtom);
	return normalizeHex(
		isDark ? scheme.highlightDark : scheme.highlightLight,
		LEGACY_HIGHLIGHT_HEX,
	);
});

export interface ResolvedTimingColors {
	start: string;
	end: string;
}

/** Start/end timing chip hexes (shared across appearances) */
export const resolvedTimingAtom = atom((get): ResolvedTimingColors => ({
	start: normalizeHex(get(activeThemeSchemeAtom).timingStart, LEGACY_TIMING_START_HEX),
	end: normalizeHex(get(activeThemeSchemeAtom).timingEnd, LEGACY_TIMING_END_HEX),
}));

export const createNewScheme = (name: string): ThemeScheme => ({
	id: uid(),
	name: name.trim() || "Untitled scheme",
	light: "green",
	dark: "jade",
	useCustomLight: false,
	useCustomDark: false,
	highlightLight: "#3e63dd",
	highlightDark: "#3e63dd",
	timingStart: LEGACY_TIMING_START_HEX,
	timingEnd: LEGACY_TIMING_END_HEX,
});
