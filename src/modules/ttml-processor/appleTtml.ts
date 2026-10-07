/*
 * Copyright 2023-2025 Steve Xiao (stevexmh@qq.com) and contributors.
 *
 * 本源代码文件是属于 AMLL TTML Tool 项目的一部分。
 * This source code file is a part of AMLL TTML Tool project.
 * 本项目的源代码的使用受到 GNU GENERAL PUBLIC LICENSE version 3 许可证的约束，具体可以参阅以下链接。
 * Use of this source code is governed by the GNU GPLv3 license that can be found through the following link.
 *
 * https://github.com/amll-dev/amll-ttml-tool/blob/main/LICENSE
 */

import type { TTMLLyric, LyricLine, LyricWord } from "$/types/ttml";

export interface AppleTtmlOptions {
	lang?: string;
}

/**
 * Format milliseconds to HH:MM:SS.mmm, zero-padded, always 3 ms digits.
 */
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
	// also check "lang" custom key
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
	// Legacy import represents <p></p> as words:[{word:""}]
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
		// non-BG
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
			// preserve exactly as plain text (usually single space, but keep as-is)
			out += w.word;
		} else {
			out += `<span begin="${formatTime(w.startTime)}" end="${formatTime(w.endTime)}">${escapeXml(w.word)}</span>`;
			// note: spaces between words are represented as separate whitespace-word entries,
			// so we don't inject extra spaces here. The inter-word space will be the next
			// whitespace word's plain text node, yielding </span> <span ...>
		}
	}
	return out;
}

function buildMainContent(words: LyricWord[]): string {
	return buildSpansContent(words);
}

function buildBgInnerContent(bgWords: LyricWord[]): string {
	// Safety net: the WASM importer strips outer BG parentheses ("(x)" -> "x").
	// Repair-on-import normally restores them, but ensure here too so a legacy
	// file re-exports with "(...)" even if it bypassed the repair path.
	// Idempotent: never double-add.
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

/**
 * Warn when a line is one single span covering the whole line (no word-level timing),
 * because Cider cannot animate it per syllable.
 */
export function getAppleTTMLWarnings(lyric: TTMLLyric): string[] {
	const warnings: string[] = [];
	for (let idx = 0; idx < lyric.lyricLines.length; idx++) {
		const line = lyric.lyricLines[idx];
		if (line.isBG) continue;
		const nonWs = getTimingWords(line.words);
		if (nonWs.length === 1 && line.words.length === 1) {
			const w = nonWs[0];
			// if word timing equals line timing (or line start/end are 0 and word covers)
			// treat as single-span line
			if (w.startTime === line.startTime && w.endTime === line.endTime) {
				warnings.push(`Line ${idx + 1} is a single span covering the whole line — Cider cannot animate per syllable`);
			} else if (line.startTime === 0 && line.endTime === 0) {
				// still line timing not set but single word — often indicates lack of word timing
				// we check if there's effectively no segmentation
				warnings.push(`Line ${idx + 1} has only one word — consider splitting for per-syllable animation`);
			}
		}
	}
	return warnings;
}

/**
 * Pure Apple Music-style TTML serializer.
 * Rules 1-9 from spec.
 */
export function toAppleTTML(lyric: TTMLLyric, opts?: AppleTtmlOptions): string {
	const lang = resolveLang(lyric, opts);
	const groups = groupLines(lyric.lyricLines);

	// Agent detection for duet head
	const agentOf = (g: Group): string => {
		if (g.main) return g.main.isDuet ? "v2" : "v1";
		if (g.bgLines.length > 0) return g.bgLines[0].isDuet ? "v2" : "v1";
		return "v1";
	};
	const usedAgents = new Set<string>(groups.map(agentOf));
	// Filter to only agents that actually appear when groups not empty; if lyric has no lines, single
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
			// standalone BG batch
			const flatBg = g.bgLines.flatMap((l) => l.words);
			const timingWords = getTimingWords(flatBg);
			if (timingWords.length === 0) {
				// fallback to first bg line times
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
			// standalone BG still needs wrapper timed to p
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
				// ensure exactly one space before BG wrapper
				if (mainContent === "") {
					// no main content, no leading space needed, but still need wrapper alone
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

		// Empty interlude <p></p> carries no agent in Apple reference files
		const agentAttr = isDuet && !g.isEmpty ? ` ttm:agent="${agentOf(g)}"` : "";
		divContentParts.push(`<p begin="${pBegin}" end="${pEnd}"${agentAttr}>${inner}</p>`);
	}

	const body = `<body><div>${divContentParts.join("")}</div></body>`;
	const ttAttrs = `xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Word" xml:lang="${escapeXml(lang)}"`;
	return `<?xml version="1.0" encoding="UTF-8"?><tt ${ttAttrs}>${head}${body}</tt>`;
}
