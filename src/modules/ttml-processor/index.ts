import {
	TranslationOutputMode,
	translationOutputModeAtom,
} from "$/modules/settings/states";
import { globalStore } from "$/states/store.ts";
import type {
	LyricLine as AppLyricLine,
	LyricWord as AppLyricWord,
	TTMLLyric as AppTTMLLyric,
	TTMLMetadata as AppTTMLMetadata,
} from "$/types/ttml";
import type {
	AmllLyricLine,
	AmllLyricResult,
	AmllLyricWord,
	AmllMetadata,
	AmllToTtmlOptions,
	GeneratorConfig,
	JsError,
	TTMLResult,
	TtmlToAmllOptions,
} from "./types";
import {
	amllToTtml as rawAmllToTtml,
	amllToTtmlResult as rawAmllToTtmlResult,
	generateTtml as rawGenerateTtml,
	parseTtml as rawParseTtml,
	ttmlResultToAmll as rawTtmlResultToAmll,
	ttmlToAmll as rawTtmlToAmll,
} from "./wasm/ttml_processor_wasm";

export type Result<T> =
	| { success: true; data: T }
	| { success: false; error: JsError };

const IGNORED_METADATA_KEYS = new Set(["timingMode", "language"]);

const RENAMED_METADATA_KEYS: Record<string, string> = {
	songwriters: "songwriter",
	title: "musicName",
};

const EXPORT_RENAMED_METADATA_KEYS: Record<string, string> = {
	songwriter: "songwriters",
};

export function getDefaultGeneratorConfig(): Partial<GeneratorConfig> {
	return {
		useAppleFormatRules:
			globalStore.get(translationOutputModeAtom) ===
			TranslationOutputMode.AppleMusic,
	};
}

function withDefaultGeneratorConfig(
	config?: Partial<GeneratorConfig>,
): Partial<GeneratorConfig> {
	return { ...getDefaultGeneratorConfig(), ...config };
}

export function parseTTML(ttmlContent: string): Result<TTMLResult> {
	return rawParseTtml(ttmlContent) as Result<TTMLResult>;
}

export function generateTTML(
	result: TTMLResult,
	config?: Partial<GeneratorConfig>,
): Result<string> {
	return rawGenerateTtml(result, config) as Result<string>;
}

export function ttmlToAmll(
	ttmlContent: string,
	options?: Partial<TtmlToAmllOptions>,
): Result<AmllLyricResult> {
	const result = rawTtmlToAmll(ttmlContent, options) as Result<AmllLyricResult>;
	if (!result.success) return result;

	const processedLyricLines = result.data.lyricLines.map((line) => {
		const newWords: AmllLyricWord[] = [];

		for (const word of line.words) {
			const trailingSpaceMatch = word.word.match(/(\s+)$/);

			if (trailingSpaceMatch && word.word !== trailingSpaceMatch[0]) {
				const spaces = trailingSpaceMatch[0];
				const pureWord = word.word.slice(0, -spaces.length);

				newWords.push({ ...word, word: pureWord });

				newWords.push({
					startTime: 0,
					endTime: 0,
					word: spaces,
				});
			} else {
				newWords.push(word);
			}
		}

		return {
			...line,
			words: newWords,
		};
	});

	return {
		success: true,
		data: {
			...result.data,
			lyricLines: processedLyricLines,
			metadata: result.data.metadata
				.filter((meta) => !IGNORED_METADATA_KEYS.has(meta.key))
				.map((meta) => ({
					...meta,
					key: RENAMED_METADATA_KEYS[meta.key] ?? meta.key,
				})),
		},
	};
}

function normalizeExportWord(word: AppLyricWord): AmllLyricWord {
	return {
		startTime: word.startTime,
		endTime: word.endTime,
		word: word.word,
		romanWord: word.romanWord,
		obscene: word.obscene,
		emptyBeat: word.emptyBeat,
		ruby: word.ruby,
	};
}

function normalizeExportLine(line: AppLyricLine): AmllLyricLine {
	return {
		words: line.words.map(normalizeExportWord),
		translatedLyric: line.translatedLyric,
		romanLyric: line.romanLyric,
		isBG: line.isBG,
		isDuet: line.isDuet,
		startTime: line.startTime,
		endTime: line.endTime,
	};
}

function normalizeExportMetadata(metadata: AppTTMLMetadata[]): AmllMetadata[] {
	return metadata.map((meta) => ({
		key: EXPORT_RENAMED_METADATA_KEYS[meta.key] ?? meta.key,
		value: [...meta.value],
	}));
}

export function ttmlLyricToAmllResult(
	ttmlLyric: AppTTMLLyric,
): AmllLyricResult {
	return {
		lyricLines: ttmlLyric.lyricLines.map(normalizeExportLine),
		metadata: normalizeExportMetadata(ttmlLyric.metadata),
	};
}

function postProcessLyricLines(amllResult: AmllLyricResult): AmllLyricResult {
	return {
		...amllResult,
		lyricLines: amllResult.lyricLines.map((line) => ({
			...line,
			startTime: Math.round(line.startTime),
			endTime: Math.round(line.endTime),
			words: line.words.map((word) => {
				const processedWord: AmllLyricWord = {
					...word,
					startTime: Math.round(word.startTime),
					endTime: Math.round(word.endTime),
					ruby: word.ruby
						? word.ruby.map((r) => ({
								...r,
								startTime: Math.round(r.startTime),
								endTime: Math.round(r.endTime),
							}))
						: undefined,
				};

				if (processedWord.emptyBeat == null || processedWord.emptyBeat === 0) {
					processedWord.emptyBeat = undefined;
				}

				if (!processedWord.romanWord || processedWord.romanWord.trim() === "") {
					processedWord.romanWord = undefined;
				}

				return processedWord;
			}),
		})),
	};
}

export function isLineTimingLyrics(
	lyric: AmllLyricResult | AppTTMLLyric,
): boolean {
	const nonBgLines = lyric.lyricLines.filter((line) => !line.isBG);
	return (
		nonBgLines.length > 0 && nonBgLines.every((line) => line.words.length === 1)
	);
}

export function amllToTTML(
	amllResult: AmllLyricResult,
	options?: Partial<AmllToTtmlOptions>,
	config?: Partial<GeneratorConfig>,
): Result<string> {
	const processedAmllResult = postProcessLyricLines(amllResult);
	const lineTiming = isLineTimingLyrics(amllResult);
	return rawAmllToTtml(
		processedAmllResult,
		options,
		withDefaultGeneratorConfig({
			lineTiming,
			...config,
		}),
	) as Result<string>;
}

export function ttmlResultToAmll(
	ttmlResult: TTMLResult,
	options?: Partial<TtmlToAmllOptions>,
): Result<AmllLyricResult> {
	return rawTtmlResultToAmll(ttmlResult, options) as Result<AmllLyricResult>;
}

export function amllToTTMLResult(
	amllResult: AmllLyricResult,
	options?: Partial<AmllToTtmlOptions>,
): Result<TTMLResult> {
	const processedAmllResult = postProcessLyricLines(amllResult);
	return rawAmllToTtmlResult(
		processedAmllResult,
		options,
	) as Result<TTMLResult>;
}
