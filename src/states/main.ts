
import { atom } from "jotai";
import { atomWithStorage, selectAtom } from "jotai/utils";
import { REDO, UNDO, withHistory } from "jotai-history";
import { uid } from "uid";
import { identifyProject } from "$/modules/project/logic/project-info";
import type { TTMLLyric } from "../types/ttml";

export enum DarkMode {
	Auto = "auto",
	Light = "light",
	Dark = "dark",
}

export enum ToolMode {
	Edit = "edit",
	Sync = "sync",
	Preview = "preview",
}

export const toolModeAtom = atom(ToolMode.Edit);
export const darkModeAtom = atomWithStorage("darkMode", DarkMode.Auto);
export const isDarkThemeAtom = atom((get) => {
	if (get(darkModeAtom) === DarkMode.Auto) return get(autoDarkModeAtom);
	return get(darkModeAtom) === DarkMode.Dark;
});
export const autoDarkModeAtom = atom(true);

export const lyricLinesAtom = atom({
	lyricLines: [],
	metadata: [],
} as TTMLLyric);

export const projectIdAtom = atom(uid());

export const rubyWarningShownProjectIdsAtom = atom(new Set<string>());

export const projectIdentityAtom = atom((get) => {
	const lyrics = get(lyricLinesAtom);
	return identifyProject(lyrics);
});

export enum SaveStatus {
	Saved = "saved",
	Pending = "pending",
	Saving = "saving",
}

export const saveStatusAtom = atom<SaveStatus>(SaveStatus.Saved);

export const lastSavedTimeAtom = atom<number | null>(null);

export const undoableLyricLinesAtom = withHistory(lyricLinesAtom, 100);
export const isDirtyAtom = atom((get) => get(undoableLyricLinesAtom).canUndo);
export const undoLyricLinesAtom = atom(null, (_get, set) => {
	set(undoableLyricLinesAtom, UNDO);
});
export const redoLyricLinesAtom = atom(null, (_get, set) => {
	set(undoableLyricLinesAtom, REDO);
});
export const editingWordStateAtom = atom({
	wordIndex: -1,
	lineIndex: -1,
	word: "",
});
export const newLyricLinesAtom = atom(
	null,
	(
		_get,
		set,
		newState: TTMLLyric = {
			lyricLines: [],
			metadata: [],
		},
	) => {
		set(lyricLinesAtom, newState);
		set(selectedLinesAtom, new Set());
		set(selectedWordsAtom, new Set());
	},
);
export const selectedLinesAtom = atom(new Set<string>());
export const selectedWordsAtom = atom(new Set<string>());

export const saveFileNameAtom = atom("lyric.ttml");

export const showUnselectedLinesAtom = atomWithStorage(
	"showUnselectedLines",
	true,
);
export const lyricAutoFollowAtom = atomWithStorage("lyricAutoFollow", false);
export const lyricAutoFollowSmoothAtom = atomWithStorage(
	"lyricAutoFollowSmooth",
	false,
);
export const followActiveLineIndexAtom = atom<number | null>(null);
export const bgLyricIgnoreSyncAtom = atom(false);
export const showEndTimeAsDurationAtom = atom(false);

export interface EditingTimeFieldState {
	isWord: boolean;
	field: "startTime" | "endTime";
}

export const editingTimeFieldAtom = atom<EditingTimeFieldState | null>(null);

export const requestFocusAtom = atom<string | null>(null);

export const isGlobalFileDraggingAtom = atom(false);

export const isDraggingGlobalAtom = atom(false);

export const draggedCountAtom = atom(0);

export const dragSourceAtom = atom<"main" | "outline" | null>(null);

export const isCopyModeAtom = atom(false);

export const locateActionAtom = atom(0);

export const lyricLineIdsAtom = selectAtom(
	lyricLinesAtom,
	(state) => state.lyricLines.map((line) => line.id),
	(prev, next) =>
		prev.length === next.length && prev.every((id, i) => id === next[i]),
);

export const lyricIdToIndexMapAtom = selectAtom(lyricLineIdsAtom, (ids) => {
	const map = new Map<string, number>();
	ids.forEach((id, index) => {
		map.set(id, index);
	});
	return map;
});
