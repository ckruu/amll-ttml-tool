
import { ROMAJI_VOWELS } from "./TransliterationUtils";

export class TokenBuffer {
	private tokens: string[];
	private cursor: number = 0;

	constructor(tokens: string[]) {
		this.tokens = tokens;
	}

	get remaining(): number {
		return Math.max(0, this.tokens.length - this.cursor);
	}

	peek(offset: number = 0): string | undefined {
		return this.tokens[this.cursor + offset];
	}

	consume(count: number): string[] {
		if (count <= 0) return [];
		const chunk = this.tokens.slice(this.cursor, this.cursor + count);
		this.cursor += count;
		return chunk;
	}

	replaceCurrent(newValue: string): void {
		if (this.cursor < this.tokens.length) {
			this.tokens[this.cursor] = newValue;
		}
	}
}

export function autoSplitRomajiChunk(token: string): string[] | null {
	const stripped = token.trim();
	if (!/^[a-zA-Z]+$/.test(stripped)) {
		return null;
	}

	let vowelCount = 0;
	const lower = stripped.toLowerCase();
	for (const char of lower) {
		if (ROMAJI_VOWELS.has(char)) vowelCount++;
	}

	if (vowelCount < 1) return null;

	const segments: string[] = [];
	let idx = 0;
	const len = stripped.length;

	while (idx < len) {
		const start = idx;
		const currentChar = lower[idx];
		const nextChar = idx + 1 < len ? lower[idx + 1] : "";

		const isConsonant = !ROMAJI_VOWELS.has(currentChar);
		if (isConsonant && currentChar === nextChar) {
			segments.push(stripped.slice(start, idx + 1));
			idx++;
			continue;
		}

		if (currentChar === "n") {
			const nextNext = idx + 2 < len ? lower[idx + 2] : "";

			const isNextVowel = ROMAJI_VOWELS.has(nextChar);
			const isNextYaYuYo = nextChar === "y" && ROMAJI_VOWELS.has(nextNext);

			if (!isNextVowel && !isNextYaYuYo) {
				segments.push(stripped.slice(start, idx + 1));
				idx++;
				continue;
			}
		}

		while (idx < len && !ROMAJI_VOWELS.has(lower[idx])) {
			idx++;
		}

		if (idx < len) {
			idx++;
		}

		segments.push(stripped.slice(start, idx));
	}

	if (segments.length <= 1) return null;

	return segments;
}

export function tokenizeRomanLyric(
	text: string,
	aggressiveSplit = true,
): TokenBuffer {
	if (!text) return new TokenBuffer([]);

	const rawTokens = text
		.trim()
		.split(/[\s\t]+/)
		.filter((t) => t);

	const refinedTokens: string[] = [];
	for (const raw of rawTokens) {
		if (aggressiveSplit) {
			const splits = autoSplitRomajiChunk(raw);
			if (splits) {
				refinedTokens.push(...splits);
			} else {
				refinedTokens.push(raw);
			}
		} else {
			refinedTokens.push(raw);
		}
	}

	return new TokenBuffer(refinedTokens);
}
