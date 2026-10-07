#!/usr/bin/env node
// Plain node verification for Apple TTML spec rules 1-7
// No test runner required — duplicates logic from src/modules/ttml-processor/appleTtml.ts
// to avoid TS loader dependency.

import assert from "node:assert/strict";

function formatTime(ms) {
	const totalMs = Math.round(ms);
	const mmm = ((totalMs % 1000) + 1000) % 1000;
	const totalSec = Math.floor(totalMs / 1000);
	const ss = ((totalSec % 60) + 60) % 60;
	const totalMin = Math.floor(totalSec / 60);
	const mm = ((totalMin % 60) + 60) % 60;
	const hh = Math.floor(totalMin / 60);
	return `${String(hh).padStart(2, "0")}:${String(mm).padStart(2, "0")}:${String(ss).padStart(2, "0")}.${String(mmm).padStart(3, "0")}`;
}
function escapeXml(s) {
	return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}
function isWhitespaceWord(w) { return w.word.trim() === ""; }
function resolveLang(lyric, opts) {
	if (opts?.lang && opts.lang.trim()) return opts.lang.trim();
	const langMeta = lyric.metadata.find((m) => m.key.toLowerCase() === "language");
	if (langMeta?.value[0]?.trim()) return langMeta.value[0].trim();
	const lang2 = lyric.metadata.find((m) => m.key.toLowerCase() === "lang");
	if (lang2?.value[0]?.trim()) return lang2.value[0].trim();
	return "en";
}
function isEmptyLine(line) {
	if (line.words.length === 0) return true;
	return line.words.every((w) => w.word === "");
}
function groupLines(lines) {
	const groups = [];
	let i = 0;
	while (i < lines.length) {
		const line = lines[i];
		if (line.isBG) {
			const batch = [];
			while (i < lines.length && lines[i].isBG) { batch.push(lines[i]); i++; }
			groups.push({ main: null, bgLines: batch, isEmpty: false });
			continue;
		}
		if (isEmptyLine(line)) {
			groups.push({ main: line, bgLines: [], isEmpty: true });
			i++; continue;
		}
		const batch = [];
		let j = i + 1;
		while (j < lines.length && lines[j].isBG) { batch.push(lines[j]); j++; }
		groups.push({ main: line, bgLines: batch, isEmpty: false });
		i = j;
	}
	return groups;
}
function buildSpansContent(words) {
	let out = "";
	for (const w of words) {
		if (isWhitespaceWord(w)) out += w.word;
		else out += `<span begin="${formatTime(w.startTime)}" end="${formatTime(w.endTime)}">${escapeXml(w.word)}</span>`;
	}
	return out;
}
function buildBgInnerContent(bgWords) {
	const timingIdx = [];
	for (let i = 0; i < bgWords.length; i++) if (!isWhitespaceWord(bgWords[i])) timingIdx.push(i);
	if (timingIdx.length > 0) {
		const firstIdx = timingIdx[0], lastIdx = timingIdx[timingIdx.length - 1];
		const next = [...bgWords];
		if (firstIdx === lastIdx) {
			let w = next[firstIdx].word;
			if (!/^[(（]/.test(w)) w = `(${w}`;
			if (!/[)）]$/.test(w)) w = `${w})`;
			next[firstIdx] = { ...next[firstIdx], word: w };
		} else {
			if (!/^[(（]/.test(next[firstIdx].word)) next[firstIdx] = { ...next[firstIdx], word: `(${next[firstIdx].word}` };
			if (!/[)）]$/.test(next[lastIdx].word)) next[lastIdx] = { ...next[lastIdx], word: `${next[lastIdx].word})` };
		}
		return buildSpansContent(next);
	}
	return buildSpansContent(bgWords);
}
function toAppleTTML(lyric, opts) {
	const lang = resolveLang(lyric, opts);
	const groups = groupLines(lyric.lyricLines);
	const agentOf = (g) => {
		if (g.main) return g.main.isDuet ? "v2" : "v1";
		if (g.bgLines.length > 0) return g.bgLines[0].isDuet ? "v2" : "v1";
		return "v1";
	};
	const hasV1 = groups.some((g) => agentOf(g) === "v1");
	const hasV2 = groups.some((g) => agentOf(g) === "v2");
	const distinctAgents = [];
	if (hasV1) distinctAgents.push("v1");
	if (hasV2) distinctAgents.push("v2");
	const isDuet = distinctAgents.length > 1;
	let head = "";
	if (isDuet) {
		const agentsXml = distinctAgents.map((id) => `<ttm:agent type="person" xml:id="${id}"/>`).join("");
		head = `<head><metadata>${agentsXml}</metadata></head>`;
	}
	const divParts = [];
	for (const g of groups) {
		let pBegin, pEnd, inner = "";
		if (g.isEmpty && g.main) {
			pBegin = formatTime(g.main.startTime);
			pEnd = formatTime(g.main.endTime);
			inner = "";
		} else if (g.main === null) {
			const flatBg = g.bgLines.flatMap((l) => l.words);
			const timing = flatBg.filter((w) => !isWhitespaceWord(w));
			if (timing.length === 0) { pBegin = formatTime(g.bgLines[0].startTime); pEnd = formatTime(g.bgLines[0].endTime); }
			else { pBegin = formatTime(Math.min(...timing.map((w) => w.startTime))); pEnd = formatTime(Math.max(...timing.map((w) => w.endTime))); }
			const innerBg = buildBgInnerContent(flatBg);
			inner = `<span ttm:role="x-bg" begin="${pBegin}" end="${pEnd}">${innerBg}</span>`;
		} else {
			const mainWords = g.main.words;
			const flatBg = g.bgLines.flatMap((l) => l.words);
			const allTiming = [...mainWords.filter((w) => !isWhitespaceWord(w)), ...flatBg.filter((w) => !isWhitespaceWord(w))];
			if (allTiming.length === 0) { pBegin = formatTime(g.main.startTime); pEnd = formatTime(g.main.endTime); }
			else { pBegin = formatTime(Math.min(...allTiming.map((w) => w.startTime))); pEnd = formatTime(Math.max(...allTiming.map((w) => w.endTime))); }
			let mainContent = buildSpansContent(mainWords);
			if (flatBg.length > 0) {
				if (mainContent !== "") {
					const trailing = mainContent.match(/ +$/);
					if (!trailing) mainContent += " ";
					else if (trailing[0].length !== 1) mainContent = mainContent.replace(/ +$/, " ");
				}
				const innerBg = buildBgInnerContent(flatBg);
				const wrapper = `<span ttm:role="x-bg" begin="${pBegin}" end="${pEnd}">${innerBg}</span>`;
				inner = mainContent + wrapper;
			} else inner = mainContent;
		}
		const agentAttr = isDuet && !g.isEmpty ? ` ttm:agent="${agentOf(g)}"` : "";
		divParts.push(`<p begin="${pBegin}" end="${pEnd}"${agentAttr}>${inner}</p>`);
	}
	const body = `<body><div>${divParts.join("")}</div></body>`;
	const ttAttrs = `xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Word" xml:lang="${escapeXml(lang)}"`;
	return `<?xml version="1.0" encoding="UTF-8"?><tt ${ttAttrs}>${head}${body}</tt>`;
}

// ---- Test data ----
function uid() { return Math.random().toString(36).slice(2); }
function mkWord(word, s, e) { return { id: uid(), word, startTime: s, endTime: e, obscene: false, emptyBeat: 0 }; }

const singleLyric = {
	metadata: [{ key: "language", value: ["ja"] }],
	lyricLines: [
		{
			id: uid(), isBG: false, isDuet: false, startTime: 1770, endTime: 60732, translatedLyric: "", romanLyric: "", ignoreSync: false,
			words: [
				mkWord("Hello", 1770, 2000),
				mkWord(" ", 0, 0),
				mkWord("world", 2100, 3000),
				mkWord("&", 3100, 3200), // to test escaping
			]
		},
		{
			id: uid(), isBG: true, isDuet: false, startTime: 3000, endTime: 60732, translatedLyric: "", romanLyric: "", ignoreSync: false,
			words: [
				mkWord("(bg", 3000, 4000),
				mkWord(" ", 0, 0),
				mkWord("vocals)", 4100, 60732), // extends pEnd beyond main
			]
		},
		{
			id: uid(), isBG: false, isDuet: false, startTime: 70000, endTime: 80000, translatedLyric: "", romanLyric: "", ignoreSync: false,
			words: [] // empty interlude
		},
		{
			id: uid(), isBG: false, isDuet: false, startTime: 80000, endTime: 90000, translatedLyric: "", romanLyric: "", ignoreSync: false,
			words: [ mkWord("solo", 80000, 90000) ]
		},
	]
};

const singleOut = toAppleTTML(singleLyric, { lang: "ja" });
console.log("=== single singer output ===");
console.log(singleOut);

// Rule 1: root element
assert.match(singleOut, /<tt xmlns="http:\/\/www\.w3\.org\/ns\/ttml" xmlns:itunes="http:\/\/music\.apple\.com\/lyric-ttml-internal" xmlns:ttm="http:\/\/www\.w3\.org\/ns\/ttml#metadata" itunes:timing="Word" xml:lang="ja">/, "rule1 root");
assert.ok(!singleOut.includes("xmlns:tts"), "no xmlns:tts");
assert.ok(!singleOut.includes("xmlns:amll"), "no xmlns:amll");
assert.ok(!singleOut.includes("amll:meta"), "no amll:meta");
assert.ok(!singleOut.includes("iTunesMetadata"), "no iTunesMetadata");
assert.ok(!singleOut.includes("itunes:key"), "no itunes:key");
assert.ok(!singleOut.includes(' dur='), "no body dur");
assert.ok(singleOut.includes("<body><div>"), "body div structure");
assert.ok(!/<div[^>]*\bbegin=/.test(singleOut), "no begin on div");

// Rule 2/3 head: single singer -> no head, no ttm:agent
assert.ok(!singleOut.includes("<head>"), "no head for single singer");
assert.ok(!singleOut.includes("ttm:agent"), "no ttm:agent for single singer");

// Rule 4: HH:MM:SS.mmm format
const timeRe = /\d{2}:\d{2}:\d{2}\.\d{3}/g;
const times = singleOut.match(timeRe) || [];
assert.ok(times.length >= 4, "found times");
for (const t of times) assert.match(t, /^\d{2}:\d{2}:\d{2}\.\d{3}$/, `time format ${t}`);
assert.ok(singleOut.includes('begin="00:00:01.770"'), "1770 -> 00:00:01.770");
assert.ok(singleOut.includes('end="00:01:00.732"'), "60732 -> 00:01:00.732");

// Rule 5: p range covering BG (BG ends after main)
assert.ok(singleOut.includes('<p begin="00:00:01.770" end="00:01:00.732"'), "p covers BG late end");

// Rule 6: BG wrapper timed and preceded by exactly one space
assert.ok(singleOut.includes('</span> <span ttm:role="x-bg" begin="00:00:01.770" end="00:01:00.732">'), "BG wrapper timed and preceded by one space");
assert.ok(!singleOut.includes('</span><span ttm:role="x-bg"'), "not directly against span");
const bgWrapperMatches = [...singleOut.matchAll(/<span ttm:role="x-bg" begin="([^"]+)" end="([^"]+)">/g)];
assert.equal(bgWrapperMatches.length, 1, "one BG wrapper");
assert.equal(bgWrapperMatches[0][1], "00:00:01.770", "BG wrapper begin equals p begin");
assert.equal(bgWrapperMatches[0][2], "00:01:00.732", "BG wrapper end equals p end");
assert.ok(singleOut.includes('<span begin="00:00:03.000" end="00:00:04.000">(bg</span>'), "BG inner word timing");
assert.ok(singleOut.includes('<span begin="00:00:04.100" end="00:01:00.732">vocals)</span>'), "BG inner second word");

// Rule 7: spaces between words as plain text nodes, no pretty-print
assert.ok(singleOut.includes('</span> <span begin="00:00:02.100"'), "space between main words");
assert.ok(!singleOut.includes("\n"), "no pretty-print newlines");
assert.equal(singleOut, singleOut.trim(), "no extra whitespace");

// Rule 8: empty interlude preserved
assert.ok(singleOut.includes('<p begin="00:01:10.000" end="00:01:20.000"></p>'), "empty interlude preserved");

// Rule 9: escaping
assert.ok(singleOut.includes('<span begin="00:00:03.100" end="00:00:03.200">&amp;</span>'), "escaping &");

// Single-span warning case
const singleSpanLyric = {
	metadata: [],
	lyricLines: [{
		id: uid(), isBG: false, isDuet: false, startTime: 0, endTime: 5000, translatedLyric: "", romanLyric: "", ignoreSync: false,
		words: [ mkWord("OnlyOneWordLine", 0, 5000) ]
	}]
};
const singleSpanOut = toAppleTTML(singleSpanLyric);
console.log("\n=== single span line output ===");
console.log(singleSpanOut);
assert.ok(singleSpanOut.includes('<p begin="00:00:00.000" end="00:00:05.000"><span begin="00:00:00.000" end="00:00:05.000">OnlyOneWordLine</span></p>'), "single span line");

// Duet case: minimal head
const duetLyric = {
	metadata: [{ key: "language", value: ["en"] }],
	lyricLines: [
		{ id: uid(), isBG: false, isDuet: false, startTime: 0, endTime: 1000, translatedLyric: "", romanLyric: "", ignoreSync: false, words: [ mkWord("hello", 0, 1000) ] },
		{ id: uid(), isBG: false, isDuet: true, startTime: 1000, endTime: 2000, translatedLyric: "", romanLyric: "", ignoreSync: false, words: [ mkWord("world", 1000, 2000) ] },
	]
};
const duetOut = toAppleTTML(duetLyric, { lang: "en" });
console.log("\n=== duet output ===");
console.log(duetOut);
assert.ok(duetOut.includes("<head><metadata>"), "duet has head");
assert.ok(duetOut.includes('<ttm:agent type="person" xml:id="v1"/>'), "duet head v1");
assert.ok(duetOut.includes('<ttm:agent type="person" xml:id="v2"/>'), "duet head v2");
assert.ok(duetOut.includes('<p begin="00:00:00.000" end="00:00:01.000" ttm:agent="v1">'), "p v1 agent");
assert.ok(duetOut.includes('<p begin="00:00:01.000" end="00:00:02.000" ttm:agent="v2">'), "p v2 agent");
// Ensure head only lists used agents (already 2)
assert.equal((duetOut.match(/ttm:agent/g) || []).length, 4, "2 in head + 2 on p = 4");

// Special chars escaping < >
const escLyric = {
	metadata: [],
	lyricLines: [{ id: uid(), isBG: false, isDuet: false, startTime: 0, endTime: 1000, translatedLyric: "", romanLyric: "", ignoreSync: false, words: [ mkWord("<tag>", 0, 500), mkWord(" ", 0, 0), mkWord("a>b", 500, 1000) ] }]
};
const escOut = toAppleTTML(escLyric);
assert.ok(escOut.includes("&lt;tag&gt;"), "escape < >");
assert.ok(escOut.includes("a&gt;b"), "escape > in word");

// Empty line represented as words:[{word:""}] (legacy WASM shape) stays empty
const emptyWordLyric = {
	metadata: [],
	lyricLines: [{ id: uid(), isBG: false, isDuet: false, startTime: 174938, endTime: 203137, translatedLyric: "", romanLyric: "", ignoreSync: false, words: [{ id: uid(), word: "", startTime: 174938, endTime: 203137, obscene: false, emptyBeat: 0 }] }]
};
const emptyWordOut = toAppleTTML(emptyWordLyric);
assert.ok(emptyWordOut.includes('<p begin="00:02:54.938" end="00:03:23.137"></p>'), "blank-word line exported as empty interlude");

// BG parens safety net: stripped BG regains parentheses on export
const strippedBgLyric = {
	metadata: [],
	lyricLines: [
		{ id: uid(), isBG: false, isDuet: false, startTime: 30407, endTime: 33406, translatedLyric: "", romanLyric: "", ignoreSync: false, words: [ mkWord("X", 30407, 30504) ] },
		{ id: uid(), isBG: true, isDuet: false, startTime: 33064, endTime: 33406, translatedLyric: "", romanLyric: "", ignoreSync: false, words: [ mkWord("bg", 33064, 33406) ] },
	]
};
const strippedBgOut = toAppleTTML(strippedBgLyric);
assert.ok(strippedBgOut.includes('<span begin="00:00:33.064" end="00:00:33.406">(bg)</span>'), "BG parens restored");

// ---- Legacy compat: detection + end-to-end open-old-then-export-fixed ----
import { ttmlToAmll as wasmTtmlToAmll, parseTtml as wasmParseTtml } from "../src/modules/ttml-processor/wasm/ttml_processor_wasm.js";

function detectLegacyTTML(text) {
	const reasons = [];
	if (text.includes("xmlns:amll")) reasons.push("xmlns:amll namespace");
	if (text.includes("xmlns:tts")) reasons.push("xmlns:tts namespace");
	if (text.includes("amll:meta")) reasons.push("amll:meta entries");
	if (text.includes("itunes:key")) reasons.push("itunes:key attributes");
	if (text.includes("iTunesMetadata") || text.includes("songwriters")) reasons.push("iTunesMetadata/songwriters");
	if (/<body[^>]*\bdur\s*=/.test(text)) reasons.push("body dur attribute");
	if (/<div[^>]*\b(begin|end)\s*=/.test(text)) reasons.push("begin/end on <div>");
	const bgWrappers = [...text.matchAll(/<span[^>]*ttm:role\s*=\s*["']x-bg["'][^>]*>/g)];
	if (bgWrappers.length > 0) {
		if (bgWrappers.some((m) => !/\bbegin\s*=/.test(m[0]) || !/\bend\s*=/.test(m[0]))) reasons.push("untimed x-bg wrapper");
		if (/<\/span><span[^>]*ttm:role\s*=\s*["']x-bg["']/.test(text)) reasons.push("x-bg glued without preceding space");
	}
	if (/(begin|end)\s*=\s*"(\d+\.\d+|\d+:\d{2}\.\d+)"/.test(text)) reasons.push("short timestamp format");
	return { isLegacy: reasons.length > 0, reasons };
}
function extractXmlLang(text) {
	const m = /<tt\b[^>]*\bxml:lang\s*=\s*"([^"]+)"/i.exec(text) ?? /<tt\b[^>]*\bxml:lang\s*=\s*'([^']+)'/i.exec(text);
	return m?.[1]?.trim() || undefined;
}

const legacySample = `<tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:tts="http://www.w3.org/ns/ttml#styling" xmlns:amll="http://www.example.com/ns/amll"><head><metadata><ttm:agent type="person" xml:id="v1"/><ttm:agent type="person" xml:id="v2"/><amll:meta key="musicName" value="x"/><iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal"><songwriters><songwriter>Y</songwriter></songwriters></iTunesMetadata></metadata></head><body dur="3:58.613"><div begin="1.770" end="3:58.613"><p begin="1.770" end="7.610" itunes:key="L1" ttm:agent="v1"><span begin="1.770" end="1.990">A</span></p><p begin="30.407" end="33.406" itunes:key="L9" ttm:agent="v2"><span begin="30.407" end="30.504">X</span><span ttm:role="x-bg"><span begin="33.064" end="33.406">(bg)</span></span></p><p begin="2:54.938" end="3:23.137" itunes:key="L50" ttm:agent="v1"></p></div></body></tt>`;
const det = detectLegacyTTML(legacySample);
assert.ok(det.isLegacy, "legacy sample detected");
for (const r of ["xmlns:amll namespace", "xmlns:tts namespace", "amll:meta entries", "itunes:key attributes", "iTunesMetadata/songwriters", "body dur attribute", "begin/end on <div>", "untimed x-bg wrapper", "x-bg glued without preceding space", "short timestamp format"]) {
	assert.ok(det.reasons.includes(r), `legacy reason: ${r}`);
}
assert.equal(extractXmlLang(legacySample), undefined, "old sample carries no xml:lang");
assert.equal(extractXmlLang(`<tt xmlns="http://www.w3.org/ns/ttml" xml:lang="ja">`), "ja", "extract xml:lang");

// End-to-end: WASM import -> repair (mirror legacyCompat.ts) -> toAppleTTML
const imp = wasmTtmlToAmll(legacySample, undefined);
assert.ok(imp.success, "legacy WASM import succeeds");
let amllLines = imp.data.lyricLines.map((l) => ({ ...l, words: l.words.map((w) => ({ ...w })) }));
// repair: agentId -> isDuet
{
	const parsed = wasmParseTtml(legacySample);
	assert.ok(parsed.success, "parseTtml succeeds");
	const agentIds = parsed.data.lines.map((l) => l.agentId ?? null);
	let pIndex = 0, fixed = 0;
	let currentAgent = agentIds[0] ?? null;
	for (const line of amllLines) {
		if (!line.isBG) { currentAgent = agentIds[pIndex] ?? null; pIndex++; }
		if (currentAgent !== null && currentAgent !== "v1" && currentAgent !== "" && !line.isBG && !line.isDuet) { line.isDuet = true; fixed++; }
	}
	// WASM already maps v2->isDuet when <head> declares agents, so repair is
	// normally a no-op safety net (fixed==0); assert final state instead.
	assert.equal(amllLines[1].isDuet, true, "v2 main line is duet");
	assert.equal(amllLines[0].isDuet, false, "v1 line is not duet");
}
// repair: BG parens + empty normalization (mirror)
for (const line of amllLines) {
	if (!line.isBG) continue;
	const timing = line.words.filter((w) => w.word.trim() !== "");
	if (timing.length === 0) continue;
	if (!/^[(（]/.test(timing[0].word)) timing[0].word = `(${timing[0].word}`;
	const last = timing[timing.length - 1];
	if (!/[)）]$/.test(last.word)) last.word = `${last.word})`;
}
for (const line of amllLines) if (line.words.length > 0 && line.words.every((w) => w.word === "")) line.words = [];
const repairedLyric = { metadata: [{ key: "language", value: ["ja"] }], lyricLines: amllLines.map((l) => ({ ...l, id: uid(), ignoreSync: false, translatedLyric: l.translatedLyric ?? "", romanLyric: l.romanLyric ?? "", words: l.words.map((w) => ({ ...w, id: uid(), obscene: false, emptyBeat: 0 })) })) };
const fixedOut = toAppleTTML(repairedLyric, { lang: "ja" });
assert.ok(!fixedOut.includes("xmlns:amll") && !fixedOut.includes("xmlns:tts"), "fixed: no legacy namespaces");
assert.ok(!fixedOut.includes("amll:meta") && !fixedOut.includes("itunes:key") && !fixedOut.includes("iTunesMetadata"), "fixed: no legacy metadata/keys");
assert.ok(!fixedOut.includes(' dur='), "fixed: no body dur");
assert.ok(!/<div[^>]*\bbegin=/.test(fixedOut), "fixed: no div begin");
assert.ok(fixedOut.includes('xml:lang="ja"') && fixedOut.includes('itunes:timing="Word"'), "fixed: root attrs");
assert.ok(fixedOut.includes("<head><metadata>"), "fixed: duet head kept (v1+v2 used)");
// Note: legacySample L1 is truncated to one span, so p end derives from words
// (spec rule 5: p spans earliest word begin to latest word end), not the old p attr.
assert.ok(fixedOut.includes('<p begin="00:00:01.770" end="00:00:01.990" ttm:agent="v1">'), "fixed: short timestamp reformatted");
assert.ok(fixedOut.includes('</span> <span ttm:role="x-bg" begin="00:00:30.407" end="00:00:33.406">'), "fixed: timed BG with preceding space");
assert.ok(fixedOut.includes('<p begin="00:02:54.938" end="00:03:23.137"></p>'), "fixed: empty interlude preserved without agent");
assert.ok(!fixedOut.includes("\n"), "fixed: one line");

console.log("\nAll assertions passed ✔");
