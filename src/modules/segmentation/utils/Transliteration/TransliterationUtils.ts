import * as wanakana from "wanakana";
import { ALL_PUNCTUATION } from "../charUtils";

export const ROMAJI_VOWELS = new Set(["a", "e", "i", "o", "u"]);

export const SOKUON_CHARS = new Set(["っ", "ッ"]);

export function isPunctuationChar(char: string): boolean {
	return ALL_PUNCTUATION.has(char);
}

export function isPunctuationOnly(text: string): boolean {
	if (!text) return false;
	for (const char of text) {
		if (!isPunctuationChar(char)) {
			return false;
		}
	}
	return true;
}

export function isSokuon(char: string): boolean {
	return SOKUON_CHARS.has(char);
}

export function shouldApplyDictionaryFallback(text: string): boolean {
	if (!text) return false;
	const cleanText = text.replace(/[、，。！？\s]/g, "");
	return wanakana.isJapanese(cleanText);
}

export function getDictionaryRomaji(text: string): string {
	return wanakana.toRomaji(text, { upcaseKatakana: false });
}

export function isChoonpu(char: string): boolean {
	return char === "ー";
}

export function isKanaOnly(text: string): boolean {
	if (!text) return false;
	const cleanText = text.replace(/[、，。！？\s]/g, "");
	return wanakana.isKana(cleanText);
}
