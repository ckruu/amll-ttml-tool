import type { ProjectInfo } from "$/modules/project/autosave/autosave";
import type { TTMLLyric } from "$/types/ttml";

export function isProjectMatch(
	dbProject: ProjectInfo,
	fileLyric: TTMLLyric,
): boolean {
	const dbMeta = dbProject.latestState.metadata;

	if (!dbMeta || dbMeta.length === 0) {
		return false;
	}

	for (const dbItem of dbMeta) {
		const dbKey = dbItem.key.trim().toLowerCase();
		const dbValues = dbItem.value.map((v) => v.trim()).filter((v) => v !== "");

		if (dbValues.length === 0) {
			continue;
		}

		const fileItem = fileLyric.metadata.find(
			(m) => m.key.trim().toLowerCase() === dbKey,
		);

		if (!fileItem) {
			return false;
		}

		const fileValues = fileItem.value
			.map((v) => v.trim())
			.filter((v) => v !== "");

		const isSubset = dbValues.every((dbVal) => fileValues.includes(dbVal));

		if (!isSubset) {
			return false;
		}
	}

	return true;
}
