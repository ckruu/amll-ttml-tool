import { CharType } from "../types";

export const RE_WHITESPACE = /[\s\n\t]/;
export const RE_LATIN =
	/[\p{Script=Latin}\p{Script=Cyrillic}\p{Script=Greek}\p{M}']/u;
export const RE_NUMERIC = /[0-9]/;

export function getCharType(c: string): CharType {
	if (!c || c.trim() === "") {
		return CharType.Whitespace;
	}
	const code = c.charCodeAt(0);

	if (RE_WHITESPACE.test(c)) return CharType.Whitespace;
	if (RE_LATIN.test(c)) return CharType.Latin;
	if (RE_NUMERIC.test(c)) return CharType.Numeric;

	if (
		(code >= 0x4e00 && code <= 0x9fff) ||
		(code >= 0x3040 && code <= 0x309f) ||
		(code >= 0x30a0 && code <= 0x30ff) ||
		(code >= 0xac00 && code <= 0xd7af)
	) {
		return CharType.Cjk;
	}
	return CharType.Other;
}

export enum MergeDirection {
	Left,
	Right,
}

export const PUNCT_LEFT = new Set([
	"(",
	"[",
	"{",
	"<",
	"「",
	"『",
	"（",
	"【",
	"《",
	"〈",
	"〔",
	"｢",
	"“",
	"‘",
	"«",
	"‹",
	"¿",
	"¡",
]);

export const PUNCT_RIGHT = new Set([
	")",
	"]",
	"}",
	">",
	"」",
	"』",
	"）",
	"】",
	"》",
	"〉",
	"〕",
	"｣",
	"”",
	"’",
	"»",
	"›",
]);

export const PUNCT_AMBIGUOUS_QUOTES = new Set(['"']);

export const PUNCT_EXTRA = new Set([
	",",
	"，",
	"。",
	"．",
	".",
	"?",
	"!",
	"！",
	"？",
	"、",
	"；",
	";",
	":",
	"：",
	"'",
	"’",
	"〜",
	"～",
	"-",
	"—",
	"…",
	"⋯",
	"·",
	"♪",
]);

export const ALL_PUNCTUATION = new Set([
	...PUNCT_LEFT,
	...PUNCT_RIGHT,
	...PUNCT_AMBIGUOUS_QUOTES,
	...PUNCT_EXTRA,
]);

export function getMergeDirection(text: string): MergeDirection {
	const firstChar = text.trim().charAt(0);

	if (PUNCT_LEFT.has(firstChar)) {
		return MergeDirection.Right;
	}

	return MergeDirection.Left;
}

export function escapeRegExp(string: string): string {
	return string.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
