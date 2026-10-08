
import type { AmllLyricResult } from "./types";
import { parseTtml as rawParseTtml } from "./wasm/ttml_processor_wasm";

export interface LegacyDetection {
	isLegacy: boolean;
	reasons: string[];
}

export function detectLegacyTTML(text: string): LegacyDetection {
	const reasons: string[] = [];
	if (text.includes("xmlns:amll")) reasons.push("xmlns:amll namespace");
	if (text.includes("xmlns:tts")) reasons.push("xmlns:tts namespace");
	if (text.includes("amll:meta")) reasons.push("amll:meta entries");
	if (text.includes("itunes:key")) reasons.push("itunes:key attributes");
	if (text.includes("iTunesMetadata") || text.includes("songwriters"))
		reasons.push("iTunesMetadata/songwriters");
	if (/<body[^>]*\bdur\s*=/s.test(text)) reasons.push("body dur attribute");
	if (/<div[^>]*\b(begin|end)\s*=/s.test(text))
		reasons.push("begin/end on <div>");
	const bgWrappers = [...text.matchAll(/<span[^>]*ttm:role\s*=\s*["']x-bg["'][^>]*>/g)];
	if (bgWrappers.length > 0) {
		const untimed = bgWrappers.filter(
			(m) => !/\bbegin\s*=/.test(m[0]) || !/\bend\s*=/.test(m[0]),
		);
		if (untimed.length > 0) reasons.push("untimed x-bg wrapper");
		if (/<\/span><span[^>]*ttm:role\s*=\s*["']x-bg["']/.test(text))
			reasons.push("x-bg glued without preceding space");
	}
	if (/(begin|end)\s*=\s*"(\d+\.\d+|\d+:\d{2}\.\d+)"/.test(text))
		reasons.push("short timestamp format");
	return { isLegacy: reasons.length > 0, reasons };
}

export function extractXmlLang(text: string): string | undefined {
	const m =
		/<tt\b[^>]*\bxml:lang\s*=\s*"([^"]+)"/i.exec(text) ??
		/<tt\b[^>]*\bxml:lang\s*=\s*'([^']+)'/i.exec(text);
	const v = m?.[1]?.trim();
	return v ? v : undefined;
}

export function getLegacyFixNotice(reasons: string[]): string {
	return `Legacy TTML detected (${reasons.join(", ")}). Opening is fine — use Copy/Save Apple TTML to re-encode with fixed attributes for Cider.`;
}

function isBlankWord(w: { word: string }): boolean {
	return w.word.trim() === "";
}

export function repairLegacyImport(
	amllResult: AmllLyricResult,
	rawText: string,
): { result: AmllLyricResult; fixes: string[] } {
	const fixes: string[] = [];
	const lines = amllResult.lyricLines.map((l) => ({
		...l,
		words: l.words.map((w) => ({ ...w })),
	}));
	const result: AmllLyricResult = {
		...amllResult,
		lyricLines: lines,
		metadata: amllResult.metadata.map((m) => ({ ...m, value: [...m.value] })),
	};

	try {
		const parsed = rawParseTtml(rawText) as
			| { success: true; data: { lines: { agentId?: string }[] } }
			| { success: false; error: unknown };
		if (parsed.success) {
			const agentIds = parsed.data.lines.map((l) => l.agentId ?? null);
			let pIndex = 0;
			let currentAgent: string | null = agentIds[0] ?? null;
			let duetFixed = 0;
			for (const line of lines) {
				if (!line.isBG) {
					currentAgent = agentIds[pIndex] ?? null;
					pIndex++;
				}
				const shouldBeDuet =
					currentAgent !== null &&
					currentAgent !== "v1" &&
					currentAgent !== "";
				if (shouldBeDuet && !line.isBG && !line.isDuet) {
					line.isDuet = true;
					duetFixed++;
				}
			}
			if (duetFixed > 0) fixes.push(`restored duet flag on ${duetFixed} line(s)`);
		}
	} catch {
	}

	let parenFixed = 0;
	for (const line of lines) {
		if (!line.isBG) continue;
		const timing = line.words.filter((w) => !isBlankWord(w));
		if (timing.length === 0) continue;
		const first = timing[0];
		const last = timing[timing.length - 1];
		if (!/^[(（]/.test(first.word)) {
			first.word = `(${first.word}`;
			parenFixed++;
		}
		if (!/[)）]$/.test(last.word)) {
			last.word = `${last.word})`;
			parenFixed++;
		}
	}
	if (parenFixed > 0) fixes.push("restored BG parentheses");

	let emptied = 0;
	for (const line of lines) {
		if (line.words.length === 0) continue;
		if (line.words.every((w) => w.word === "")) {
			line.words = [];
			emptied++;
		}
	}
	if (emptied > 0) fixes.push(`preserved ${emptied} empty interlude(s)`);

	return { result, fixes };
}
