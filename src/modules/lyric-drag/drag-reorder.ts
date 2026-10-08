import { uid } from "uid";
import type { LyricLine } from "$/types/ttml.ts";

const cloneLyricLine = (line: LyricLine): LyricLine => {
	return {
		...line,
		id: uid(),
		words: line.words.map((w) => ({
			...w,
			id: uid(),
			ruby: w.ruby ? w.ruby.map((r) => ({ ...r })) : undefined,
		})),
	};
};

export const reorderOrCopyLyricLines = (
	originalLines: LyricLine[],
	draggedIds: Set<string>,
	dropIndex: number,
	isCopy: boolean,
): { nextLines: LyricLine[]; newlyCreatedIds: Set<string> } => {
	const toMove = originalLines.filter((line) => draggedIds.has(line.id));
	let remaining = originalLines;
	const newlyCreatedIds = new Set<string>();
	let finalToInsert = toMove;

	let adjustedDropIndex = dropIndex;

	if (isCopy) {
		finalToInsert = toMove.map((line) => {
			const cloned = cloneLyricLine(line);
			newlyCreatedIds.add(cloned.id);
			return cloned;
		});
	} else {
		remaining = originalLines.filter((line) => !draggedIds.has(line.id));
		let adjustCount = 0;
		for (let i = 0; i < dropIndex && i < originalLines.length; i++) {
			if (draggedIds.has(originalLines[i].id)) {
				adjustCount++;
			}
		}
		adjustedDropIndex -= adjustCount;
	}

	const safeIndex = Math.max(0, Math.min(adjustedDropIndex, remaining.length));

	const nextLines = [...remaining];
	nextLines.splice(safeIndex, 0, ...finalToInsert);

	return { nextLines, newlyCreatedIds };
};
