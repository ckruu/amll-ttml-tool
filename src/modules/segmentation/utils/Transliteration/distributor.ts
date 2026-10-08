
import type { LyricWord } from "$/types/ttml";
import {
	getDictionaryRomaji,
	isChoonpu,
	isKanaOnly,
	isPunctuationOnly,
	isSokuon,
	ROMAJI_VOWELS,
	shouldApplyDictionaryFallback,
} from "./TransliterationUtils";
import { type TokenBuffer, tokenizeRomanLyric } from "./tokenizer";

class RomanDistributor {
	private words: LyricWord[];
	private buffer: TokenBuffer;
	private results: string[];

	private pendingWords: { index: number; word: LyricWord }[] = [];

	constructor(words: LyricWord[], romanLyric: string) {
		this.words = words;
		this.buffer = tokenizeRomanLyric(romanLyric);
		this.results = new Array(words.length).fill("");
	}

	public predict(): string[] {
		if (this.buffer.remaining === 0) {
			return this.getFallbackResults();
		}

		for (let i = 0; i < this.words.length; i++) {
			const word = this.words[i];

			if (this.shouldSkipWord(word, i)) {
				continue;
			}

			if (this.tryConsumePunctuation(word, i)) {
				continue;
			}

			if (this.tryMatchAnchor(word, i)) {
				continue;
			}

			this.pendingWords.push({ index: i, word });
		}

		this.flushPendingBuffer(this.buffer.consume(this.buffer.remaining));

		return this.results;
	}

	private getFallbackResults(): string[] {
		return this.words.map((w) => {
			if (shouldApplyDictionaryFallback(w.word)) {
				return getDictionaryRomaji(w.word);
			}
			return "";
		});
	}

	private shouldSkipWord(word: LyricWord, index: number): boolean {
		const text = word.word.trim();
		if (!text) {
			this.results[index] = "";
			return true;
		}
		return false;
	}

	private tryConsumePunctuation(word: LyricWord, index: number): boolean {
		if (isPunctuationOnly(word.word)) {
			const currentToken = this.buffer.peek(0);
			if (currentToken && isPunctuationOnly(currentToken)) {
				this.results[index] = this.buffer.consume(1)[0];
			} else {
				this.results[index] = "";
			}
			return true;
		}
		return false;
	}

	private tryMatchAnchor(word: LyricWord, index: number): boolean {
		const text = word.word.trim();

		if (!isKanaOnly(text) || isSokuon(text) || isChoonpu(text)) {
			return false;
		}

		const expectedCandidates = getExpectedRomaji(text);
		const searchLimit = Math.min(this.buffer.remaining, 15);

		for (let offset = 0; offset < searchLimit; offset++) {
			const token = this.buffer.peek(offset);
			if (!token) continue;

			const matchResult = evaluateMatch(token, expectedCandidates);
			if (!matchResult) continue;

			const availableTokens = offset + (matchResult.type === "Suffix" ? 1 : 0);
			const requiredTokens = this.getPendingBufferWeight();

			if (availableTokens < requiredTokens) {
				continue;
			}

			if (!this.checkLookahead(index, matchResult.type, offset)) {
				continue;
			}

			this.applyAnchorMatch(index, offset, matchResult, token);
			return true;
		}

		return false;
	}

	private checkLookahead(
		currentIndex: number,
		matchType: MatchResult["type"],
		offset: number,
	): boolean {
		if (matchType === "Prefix") return true;

		if (currentIndex + 1 >= this.words.length) return true;

		const nextWord = this.words[currentIndex + 1];
		const nextWordText = nextWord.word.trim();

		if (
			isKanaOnly(nextWordText) &&
			!isSokuon(nextWordText) &&
			!isPunctuationOnly(nextWordText) &&
			!isChoonpu(nextWordText)
		) {
			const nextExpected = getExpectedRomaji(nextWordText);
			const nextToken = this.buffer.peek(offset + 1);

			if (nextToken) {
				if (!evaluateMatch(nextToken, nextExpected)) {
					return false;
				}
			}
		}
		return true;
	}

	private applyAnchorMatch(
		index: number,
		offset: number,
		matchResult: MatchResult,
		originalToken: string,
	): void {
		const bufferTokens = this.buffer.consume(offset);

		if (matchResult.type === "Suffix") {
			const splitIndex = originalToken.lastIndexOf(matchResult.expected);
			const prefixPart = originalToken.slice(0, splitIndex);

			bufferTokens.push(prefixPart);
			this.flushPendingBuffer(bufferTokens);

			this.results[index] = matchResult.expected;
			this.buffer.consume(1);
		} else {
			this.flushPendingBuffer(bufferTokens);

			if (matchResult.type === "Exact") {
				this.results[index] = this.buffer.consume(1)[0];
			} else {
				this.results[index] = matchResult.expected;
				const remainder = originalToken.slice(matchResult.expected.length);
				this.buffer.replaceCurrent(remainder);
			}
		}
	}

	private getPendingBufferWeight(): number {
		return this.pendingWords.reduce((sum, item) => {
			const extra = item.word.emptyBeat || 0;
			return sum + 1 + extra;
		}, 0);
	}

	private flushPendingBuffer(tokens: string[]): void {
		if (this.pendingWords.length === 0) return;

		const wordCount = this.pendingWords.length;
		const tokenCount = tokens.length;

		if (tokenCount === 0) {
			this.pendingWords.forEach((item) => {
				this.results[item.index] = "";
			});
			this.pendingWords = [];
			return;
		}

		const weights = this.pendingWords.map((item) => {
			const extra = item.word.emptyBeat || 0;
			return 1 + extra;
		});

		const totalWeight = weights.reduce((a, b) => a + b, 0);

		let currentTokenIndex = 0;
		let accumulatedWeight = 0;

		for (let i = 0; i < wordCount; i++) {
			const weight = weights[i];
			accumulatedWeight += weight;

			const targetEndIndex =
				totalWeight > 0
					? Math.round((accumulatedWeight / totalWeight) * tokenCount)
					: Math.round(((i + 1) / wordCount) * tokenCount);

			const safeEndIndex = Math.min(targetEndIndex, tokenCount);
			const takeCount = safeEndIndex - currentTokenIndex;

			if (takeCount > 0) {
				const chunk = tokens.slice(
					currentTokenIndex,
					currentTokenIndex + takeCount,
				);
				this.results[this.pendingWords[i].index] = chunk.join(" ");
				currentTokenIndex += takeCount;
			} else {
				this.results[this.pendingWords[i].index] = "";
			}
		}

		this.pendingWords = [];
	}
}

function getExpectedRomaji(text: string): string[] {
	const standard = getDictionaryRomaji(text);
	const results = new Set([standard]);

	if (text === "は") results.add("wa");
	if (text === "へ") results.add("e");
	if (text === "を") results.add("o");

	return Array.from(results);
}

interface MatchResult {
	type: "Exact" | "Prefix" | "Suffix";
	expected: string;
}

function evaluateMatch(
	token: string,
	expectedCandidates: string[],
): MatchResult | null {
	const lowerToken = token.toLowerCase();

	for (const expected of expectedCandidates) {
		if (lowerToken === expected) return { type: "Exact", expected };

		if (lowerToken.startsWith(expected) && lowerToken.length > expected.length)
			return { type: "Prefix", expected };

		if (lowerToken.endsWith(expected) && lowerToken.length > expected.length) {
			const prefix = lowerToken.slice(0, lowerToken.length - expected.length);
			const firstCharOfExpected = expected[0];
			const lastCharOfPrefix = prefix[prefix.length - 1];

			if (ROMAJI_VOWELS.has(firstCharOfExpected)) {
				const isPrefixConsonant =
					!ROMAJI_VOWELS.has(lastCharOfPrefix) && lastCharOfPrefix !== "n";

				if (isPrefixConsonant) {
					continue;
				}
			}
			return { type: "Suffix", expected };
		}
	}
	return null;
}

export function predictLineRomanization(
	words: LyricWord[],
	romanLyric: string,
): string[] {
	const distributor = new RomanDistributor(words, romanLyric);
	return distributor.predict();
}
