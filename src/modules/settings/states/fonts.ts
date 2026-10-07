import { openDB } from "idb";
import { atom } from "jotai";
import { atomWithStorage } from "jotai/utils";
import { uid } from "uid";

export type FontMode = "simple" | "advanced";

export const fontModeAtom = atomWithStorage<FontMode>(
	"settings_fontMode",
	"simple",
);

// Simple mode: one Latin + one CJK family applied across all views.
export const fontSimpleLatinAtom = atomWithStorage(
	"settings_fontLatin",
	"Inter",
);
export const fontSimpleCjkAtom = atomWithStorage(
	"settings_fontCjk",
	"MiSans",
);

// Advanced mode: per-view (edit / sync / preview) Latin + CJK families.
export const fontEditLatinAtom = atomWithStorage(
	"settings_fontEditLatin",
	"Inter",
);
export const fontEditCjkAtom = atomWithStorage("settings_fontEditCjk", "MiSans");
export const fontSyncLatinAtom = atomWithStorage(
	"settings_fontSyncLatin",
	"Inter",
);
export const fontSyncCjkAtom = atomWithStorage("settings_fontSyncCjk", "MiSans");
export const fontPreviewLatinAtom = atomWithStorage(
	"settings_fontPreviewLatin",
	"Inter",
);
export const fontPreviewCjkAtom = atomWithStorage(
	"settings_fontPreviewCjk",
	"MiSans",
);

export const LATIN_FONT_PRESETS = [
	"Inter",
	"Arial",
	"Helvetica",
	"Georgia",
	"Times New Roman",
	"Verdana",
	"system-ui",
] as const;

export const CJK_FONT_PRESETS = [
	"MiSans",
	"PingFang SC",
	"Hiragino Sans GB",
	"Microsoft YaHei",
	"Noto Sans CJK SC",
	"Source Han Sans SC",
	"SimHei",
	"sans-serif",
] as const;

export interface CustomFontEntry {
	id: string;
	/** Sanitized family name used in font-family stacks */
	family: string;
	fileName: string;
	updatedAt: number;
}

export const customFontEntriesAtom = atomWithStorage<CustomFontEntry[]>(
	"settings_customFonts",
	[],
);

type CustomFontBlobRecord = {
	id: string;
	blob: Blob;
	fileName: string;
	updatedAt: number;
};

const CUSTOM_FONT_DB = "amll-custom-fonts";
const CUSTOM_FONT_STORE = "fonts";

const customFontDbPromise =
	typeof indexedDB === "undefined"
		? null
		: openDB(CUSTOM_FONT_DB, 1, {
				upgrade(db) {
					if (!db.objectStoreNames.contains(CUSTOM_FONT_STORE)) {
						db.createObjectStore(CUSTOM_FONT_STORE, { keyPath: "id" });
					}
				},
			});

async function readFontBlob(id: string): Promise<Blob | null> {
	try {
		if (!customFontDbPromise) return null;
		const db = await customFontDbPromise;
		const record = (await db.get(
			CUSTOM_FONT_STORE,
			id,
		)) as CustomFontBlobRecord | undefined;
		return record?.blob ?? null;
	} catch {
		return null;
	}
}

async function writeFontBlob(record: CustomFontBlobRecord) {
	try {
		if (!customFontDbPromise) return;
		const db = await customFontDbPromise;
		await db.put(CUSTOM_FONT_STORE, record);
	} catch {}
}

async function deleteFontBlob(id: string) {
	try {
		if (!customFontDbPromise) return;
		const db = await customFontDbPromise;
		await db.delete(CUSTOM_FONT_STORE, id);
	} catch {}
}

export const sanitizeFontFamily = (raw: string): string =>
	raw
		.replace(/["';]/g, "")
		.replace(/\s+/g, " ")
		.trim()
		.slice(0, 80);

export const familyToCss = (family: string): string => {
	const clean = sanitizeFontFamily(family);
	if (!clean) return "sans-serif";
	// Generic families and system-ui must stay unquoted.
	if (
		/^(serif|sans-serif|monospace|cursive|fantasy|system-ui|ui-sans-serif|ui-serif|ui-monospace|ui-rounded)$/i.test(
			clean,
		)
	) {
		return clean;
	}
	return `"${clean}"`;
};

/**
 * Build a per-glyph resolving stack: Latin glyphs use the Latin font,
 * CJK glyphs fall through to the CJK font.
 */
export const buildFontStack = (latin: string, cjk: string): string =>
	`${familyToCss(latin)}, ${familyToCss(cjk)}, system-ui, sans-serif`;

// Loaded FontFace handles so entries can be removed from document.fonts.
const loadedFaces = new Map<string, { face: FontFace; url: string }>();

async function loadFontFace(
	family: string,
	blob: Blob,
): Promise<{ face: FontFace; url: string } | null> {
	try {
		const url = URL.createObjectURL(blob);
		const face = new FontFace(family, `url(${url})`, {});
		await face.load();
		document.fonts.add(face);
		return { face, url };
	} catch {
		return null;
	}
}

/** Registered custom families, for datalist options in the UI. */
export const loadedCustomFamiliesAtom = atom<string[]>([]);

export const registerCustomFontAtom = atom(
	null,
	async (get, set, file: File | Blob, fileName: string) => {
		const base = sanitizeFontFamily(
			fileName.replace(/\.[^.]+$/, "") || "Custom Font",
		);
		const taken = new Set([
			...get(customFontEntriesAtom).map((e) => e.family),
			...get(loadedCustomFamiliesAtom),
		]);
		let family = base || "Custom Font";
		let n = 2;
		while (taken.has(family)) family = `${base} ${n++}`;

		const blob = file instanceof Blob ? file : new Blob([file]);
		const loaded = await loadFontFace(family, blob);
		if (!loaded) return null;

		const id = uid();
		await writeFontBlob({ id, blob, fileName, updatedAt: Date.now() });
		loadedFaces.set(id, loaded);
		set(customFontEntriesAtom, [
			...get(customFontEntriesAtom),
			{ id, family, fileName, updatedAt: Date.now() },
		]);
		set(loadedCustomFamiliesAtom, [...get(loadedCustomFamiliesAtom), family]);
		return family;
	},
);

export const unregisterCustomFontAtom = atom(
	null,
	async (get, set, id: string) => {
		const loaded = loadedFaces.get(id);
		if (loaded) {
			try {
				document.fonts.delete(loaded.face);
			} catch {}
			URL.revokeObjectURL(loaded.url);
			loadedFaces.delete(id);
		}
		const entry = get(customFontEntriesAtom).find((e) => e.id === id);
		await deleteFontBlob(id);
		set(
			customFontEntriesAtom,
			get(customFontEntriesAtom).filter((e) => e.id !== id),
		);
		if (entry) {
			set(
				loadedCustomFamiliesAtom,
				get(loadedCustomFamiliesAtom).filter((f) => f !== entry.family),
			);
		}
	},
);

export const initCustomFontsAtom = atom(null, async (get, set) => {
	const entries = get(customFontEntriesAtom);
	const families: string[] = [];
	for (const entry of entries) {
		if (loadedFaces.has(entry.id)) {
			families.push(entry.family);
			continue;
		}
		const blob = await readFontBlob(entry.id);
		if (!blob) continue;
		const loaded = await loadFontFace(entry.family, blob);
		if (!loaded) continue;
		loadedFaces.set(entry.id, loaded);
		families.push(entry.family);
	}
	set(loadedCustomFamiliesAtom, families);
});

// Interface (whole-app chrome) font: titlebar, ribbon, sidebar, dialogs.
export const fontUiLatinAtom = atomWithStorage(
	"settings_fontUiLatin",
	"Inter",
);
export const fontUiCjkAtom = atomWithStorage("settings_fontUiCjk", "MiSans");

export interface FontStacks {
	edit: string;
	sync: string;
	preview: string;
}

export const fontStacksAtom = atom((get): FontStacks => {
	if (get(fontModeAtom) === "advanced") {
		return {
			edit: buildFontStack(get(fontEditLatinAtom), get(fontEditCjkAtom)),
			sync: buildFontStack(get(fontSyncLatinAtom), get(fontSyncCjkAtom)),
			preview: buildFontStack(
				get(fontPreviewLatinAtom),
				get(fontPreviewCjkAtom),
			),
		};
	}
	const stack = buildFontStack(
		get(fontSimpleLatinAtom),
		get(fontSimpleCjkAtom),
	);
	return { edit: stack, sync: stack, preview: stack };
});

/** Whole-app interface font stack (themed chrome outside lyric views) */
export const fontUiStackAtom = atom((get): string =>
	buildFontStack(get(fontUiLatinAtom), get(fontUiCjkAtom)),
);
