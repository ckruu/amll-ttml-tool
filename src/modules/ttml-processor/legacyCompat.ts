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

import type { AmllLyricResult } from "./types";
import { parseTtml as rawParseTtml } from "./wasm/ttml_processor_wasm";

export interface LegacyDetection {
	isLegacy: boolean;
	reasons: string[];
}

/**
 * String-level detection of legacy AMLL TOOL output that Cider cannot render.
 * Pure, no parsing — safe to run on any text.
 */
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
	// bare BG wrapper without begin/end
	const bgWrappers = [...text.matchAll(/<span[^>]*ttm:role\s*=\s*["']x-bg["'][^>]*>/g)];
	if (bgWrappers.length > 0) {
		const untimed = bgWrappers.filter(
			(m) => !/\bbegin\s*=/.test(m[0]) || !/\bend\s*=/.test(m[0]),
		);
		if (untimed.length > 0) reasons.push("untimed x-bg wrapper");
		// BG glued directly against previous span (no space)
		if (/<\/span><span[^>]*ttm:role\s*=\s*["']x-bg["']/.test(text))
			reasons.push("x-bg glued without preceding space");
	}
	// short timestamps: "1.770" or "1:00.732" instead of HH:MM:SS.mmm
	if (/(begin|end)\s*=\s*"(\d+\.\d+|\d+:\d{2}\.\d+)"/.test(text))
		reasons.push("short timestamp format");
	return { isLegacy: reasons.length > 0, reasons };
}

/** Extract xml:lang from raw TTML text (double or single quotes). */
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

/**
 * Repair the AmllLyricResult produced by the WASM importer so a legacy file
 * re-exports correctly via toAppleTTML. Pure apart from a second WASM parse
 * for agentIds (read-only).
 *
 * Fixes applied (all idempotent, no model shape change):
 * 1. agentId (v1/v2) -> isDuet. The AMLL downgrade drops agentId entirely
 *    (isDuet is always false on import), which would collapse duets to
 *    single-singer on Apple export. Recover via parseTtml agentIds.
 * 2. BG parentheses: WASM strips outer parens on import ("(x)" -> "x").
 *    Restore "(" on first BG word and ")" on last BG word when missing.
 * 3. Empty interlude <p></p> arrives as words:[{word:""}]; normalize to words:[].
 *
 * Returns the repaired result plus human-readable fix descriptions.
 */
export function repairLegacyImport(
	amllResult: AmllLyricResult,
	rawText: string,
): { result: AmllLyricResult; fixes: string[] } {
	const fixes: string[] = [];
	// shallow-clone lines/words so caller data is never mutated
	const lines = amllResult.lyricLines.map((l) => ({
		...l,
		words: l.words.map((w) => ({ ...w })),
	}));
	const result: AmllLyricResult = {
		...amllResult,
		lyricLines: lines,
		metadata: amllResult.metadata.map((m) => ({ ...m, value: [...m.value] })),
	};

	// --- 1. agentId -> isDuet ---
	try {
		const parsed = rawParseTtml(rawText) as
			| { success: true; data: { lines: { agentId?: string }[] } }
			| { success: false; error: unknown };
		if (parsed.success) {
			const agentIds = parsed.data.lines.map((l) => l.agentId ?? null);
			// Walk Amll lines; each non-BG line consumes one TTML <p>,
			// following BG lines inherit the same <p>'s agent.
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
				// Only v2 (or any non-v1 agent) maps to isDuet=true; v1/absent stays false.
				// This preserves single-singer files (all v1/absent -> no head).
				if (shouldBeDuet && !line.isBG && !line.isDuet) {
					line.isDuet = true;
					duetFixed++;
				}
			}
			if (duetFixed > 0) fixes.push(`restored duet flag on ${duetFixed} line(s)`);
		}
	} catch {
		// read-only probe failed — leave isDuet untouched
	}

	// --- 2. BG parentheses ---
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

	// --- 3. empty interlude normalization ---
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
