
import type { TTMLLyric, LyricLine, LyricWord } from "$/types/ttml";

export interface AppleTtmlOptions {
	lang?: string;
}

export function formatTime(ms: number): string {
	const totalMs = Math.round(ms);
	const mmm = ((totalMs % 1000) + 1000) % 1000;
	const totalSec = Math.floor(totalMs / 1000);
	const ss = ((totalSec % 60) + 60) % 60;
	const totalMin = Math.floor(totalSec / 60);
	const mm = ((totalMin % 60) + 60) % 60;
	const hh = Math.floor(totalMin / 60);
	return `${String(hh).padStart(2, "0")}:${String(mm).padStart(2, "0")}:${String(ss).padStart(2, "0")}.${String(mmm).padStart(3, "0")}`;
}

export function escapeXml(s: string): string {
	return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function isWhitespaceWord(w: LyricWord): boolean {
	return w.word.trim() === "";
}

function resolveLang(lyric: TTMLLyric, opts?: AppleTtmlOptions): string {
	if (opts?.lang && opts.lang.trim()) return opts.lang.trim();
	const langMeta = lyric.metadata.find((m) => m.key.toLowerCase() === "language");
	if (langMeta?.value[0]?.trim()) return langMeta.value[0].trim();
	const lang2 = lyric.metadata.find((m) => m.key.toLowerCase() === "lang");
	if (lang2?.value[0]?.trim()) return lang2.value[0].trim();
	return "en";
}

type Group = {
	main: LyricLine | null;
	bgLines: LyricLine[];
	isEmpty: boolean;
};

function isEmptyLine(line: LyricLine): boolean {
	if (line.words.length === 0) return true;
	return line.words.every((w) => w.word === "");
}

function groupLines(lines: LyricLine[]): Group[] {
	const groups: Group[] = [];
	let i = 0;
	while (i < lines.length) {
		const line = lines[i];
		if (line.isBG) {
			const batch: LyricLine[] = [];
			while (i < lines.length && lines[i].isBG) {
				batch.push(lines[i]);
				i++;
			}
			groups.push({ main: null, bgLines: batch, isEmpty: false });
			continue;
		}
		if (isEmptyLine(line)) {
			groups.push({ main: line, bgLines: [], isEmpty: true });
			i++;
			continue;
		}
		const batch: LyricLine[] = [];
		let j = i + 1;
		while (j < lines.length && lines[j].isBG) {
			batch.push(lines[j]);
			j++;
		}
		groups.push({ main: line, bgLines: batch, isEmpty: false });
		i = j;
	}
	return groups;
}

function getTimingWords(words: LyricWord[]): LyricWord[] {
	return words.filter((w) => !isWhitespaceWord(w));
}

function buildSpansContent(words: LyricWord[]): string {
	let out = "";
	for (const w of words) {
		if (isWhitespaceWord(w)) {
			out += w.word;
		} else {
			out += `<span begin="${formatTime(w.startTime)}" end="${formatTime(w.endTime)}">${escapeXml(w.word)}</span>`;
		}
	}
	return out;
}

function buildMainContent(words: LyricWord[]): string {
	return buildSpansContent(words);
}

function buildBgInnerContent(bgWords: LyricWord[]): string {
	const timingIdx: number[] = [];
	for (let i = 0; i < bgWords.length; i++) {
		if (!isWhitespaceWord(bgWords[i])) timingIdx.push(i);
	}
	if (timingIdx.length > 0) {
		const firstIdx = timingIdx[0];
		const lastIdx = timingIdx[timingIdx.length - 1];
		const next = [...bgWords];
		if (firstIdx === lastIdx) {
			let w = next[firstIdx].word;
			if (!/^[(（]/.test(w)) w = `(${w}`;
			if (!/[)）]$/.test(w)) w = `${w})`;
			next[firstIdx] = { ...next[firstIdx], word: w };
		} else {
			if (!/^[(（]/.test(next[firstIdx].word))
				next[firstIdx] = { ...next[firstIdx], word: `(${next[firstIdx].word}` };
			if (!/[)）]$/.test(next[lastIdx].word))
				next[lastIdx] = { ...next[lastIdx], word: `${next[lastIdx].word})` };
		}
		return buildSpansContent(next);
	}
	return buildSpansContent(bgWords);
}

export function getAppleTTMLWarnings(lyric: TTMLLyric): string[] {
	const warnings: string[] = [];
	for (let idx = 0; idx < lyric.lyricLines.length; idx++) {
		const line = lyric.lyricLines[idx];
		if (line.isBG) continue;
		const nonWs = getTimingWords(line.words);
		if (nonWs.length === 1 && line.words.length === 1) {
			const w = nonWs[0];
			if (w.startTime === line.startTime && w.endTime === line.endTime) {
				warnings.push(`Line ${idx + 1} is a single span covering the whole line — Cider cannot animate per syllable`);
			} else if (line.startTime === 0 && line.endTime === 0) {
				warnings.push(`Line ${idx + 1} has only one word — consider splitting for per-syllable animation`);
			}
		}
	}
	return warnings;
}

export function toAppleTTML(lyric: TTMLLyric, opts?: AppleTtmlOptions): string {
	const lang = resolveLang(lyric, opts);
	const groups = groupLines(lyric.lyricLines);

	const agentOf = (g: Group): string => {
		if (g.main) return g.main.isDuet ? "v2" : "v1";
		if (g.bgLines.length > 0) return g.bgLines[0].isDuet ? "v2" : "v1";
		return "v1";
	};
	const usedAgents = new Set<string>(groups.map(agentOf));
	const hasV1 = usedAgents.has("v1");
	const hasV2 = usedAgents.has("v2");
	const distinctAgents: string[] = [];
	if (hasV1) distinctAgents.push("v1");
	if (hasV2) distinctAgents.push("v2");
	const isDuet = distinctAgents.length > 1;

	let head = "";
	if (isDuet) {
		const agentsXml = distinctAgents
			.map((id) => `<ttm:agent type="person" xml:id="${id}"/>`)
			.join("");
		head = `<head><metadata>${agentsXml}</metadata></head>`;
	}

	const divContentParts: string[] = [];
	for (const g of groups) {
		let pBegin: string;
		let pEnd: string;
		let inner = "";

		if (g.isEmpty && g.main) {
			pBegin = formatTime(g.main.startTime);
			pEnd = formatTime(g.main.endTime);
			inner = "";
		} else if (g.main === null) {
			const flatBg = g.bgLines.flatMap((l) => l.words);
			const timingWords = getTimingWords(flatBg);
			if (timingWords.length === 0) {
				const first = g.bgLines[0];
				pBegin = formatTime(first.startTime);
				pEnd = formatTime(first.endTime);
			} else {
				const minBegin = Math.min(...timingWords.map((w) => w.startTime));
				const maxEnd = Math.max(...timingWords.map((w) => w.endTime));
				pBegin = formatTime(minBegin);
				pEnd = formatTime(maxEnd);
			}
			const innerBg = buildBgInnerContent(flatBg);
			inner = `<span ttm:role="x-bg" begin="${pBegin}" end="${pEnd}">${innerBg}</span>`;
		} else {
			const mainWords = g.main.words;
			const flatBg = g.bgLines.flatMap((l) => l.words);
			const allTimingWords = [...getTimingWords(mainWords), ...getTimingWords(flatBg)];
			if (allTimingWords.length === 0) {
				pBegin = formatTime(g.main.startTime);
				pEnd = formatTime(g.main.endTime);
			} else {
				const minBegin = Math.min(...allTimingWords.map((w) => w.startTime));
				const maxEnd = Math.max(...allTimingWords.map((w) => w.endTime));
				pBegin = formatTime(minBegin);
				pEnd = formatTime(maxEnd);
			}
			let mainContent = buildMainContent(mainWords);
			if (flatBg.length > 0) {
				if (mainContent === "") {
				} else {
					const trailing = mainContent.match(/ +$/);
					if (!trailing) {
						mainContent += " ";
					} else if (trailing[0].length !== 1) {
						mainContent = mainContent.replace(/ +$/, " ");
					}
				}
				const innerBg = buildBgInnerContent(flatBg);
				const wrapper = `<span ttm:role="x-bg" begin="${pBegin}" end="${pEnd}">${innerBg}</span>`;
				inner = mainContent + wrapper;
			} else {
				inner = mainContent;
			}
		}

		const agentAttr = isDuet && !g.isEmpty ? ` ttm:agent="${agentOf(g)}"` : "";
		divContentParts.push(`<p begin="${pBegin}" end="${pEnd}"${agentAttr}>${inner}</p>`);
	}

	const body = `<body><div>${divContentParts.join("")}</div></body>`;
	const ttAttrs = `xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Word" xml:lang="${escapeXml(lang)}"`;
	return `<?xml version="1.0" encoding="UTF-8"?><tt ${ttAttrs}>${head}${body}</tt>`;
}
