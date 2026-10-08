import { atom } from "jotai";
import { atomWithStorage } from "jotai/utils";

export type SidebarTabId = "outline" | "bpm" | "preview";

export type SidebarPanelType = "none" | SidebarTabId;

export const openSidebarTabsAtom = atomWithStorage<SidebarTabId[]>(
	"openSidebarTabs",
	["outline"],
);

const baseActiveSidebarTabAtom = atomWithStorage<SidebarPanelType>(
	"activeSidebarTab",
	"outline",
);

export const activeSidebarTabAtom = atom(
	(get) => {
		const active = get(baseActiveSidebarTabAtom);
		const openTabs = get(openSidebarTabsAtom);
		if (active !== "none" && !openTabs.includes(active as SidebarTabId)) {
			return openTabs.length > 0 ? openTabs[openTabs.length - 1] : "none";
		}
		return active;
	},
	(
		get,
		set,
		update: SidebarPanelType | ((prev: SidebarPanelType) => SidebarPanelType),
	) => {
		const nextValue =
			typeof update === "function"
				? update(get(baseActiveSidebarTabAtom))
				: update;
		set(baseActiveSidebarTabAtom, nextValue);
		if (nextValue === "none") {
			set(openSidebarTabsAtom, []);
		}
	},
);

export const openTabAtom = atom(null, (get, set, tabId: SidebarTabId) => {
	const currentOpen = get(openSidebarTabsAtom);
	if (!currentOpen.includes(tabId)) {
		set(openSidebarTabsAtom, [...currentOpen, tabId]);
	}
	set(activeSidebarTabAtom, tabId);
});

export const closeTabAtom = atom(null, (get, set, tabId: SidebarTabId) => {
	const currentOpen = get(openSidebarTabsAtom);
	const newOpen = currentOpen.filter((id) => id !== tabId);
	set(openSidebarTabsAtom, newOpen);

	const activeTab = get(activeSidebarTabAtom);
	if (activeTab === tabId) {
		if (newOpen.length > 0) {
			set(activeSidebarTabAtom, newOpen[newOpen.length - 1]);
		} else {
			set(activeSidebarTabAtom, "none");
		}
	}
});

export const toggleTabAtom = atom(
	null,
	(get, set, { tabId, open }: { tabId: SidebarTabId; open: boolean }) => {
		if (open) {
			const currentOpen = get(openSidebarTabsAtom);
			if (!currentOpen.includes(tabId)) {
				set(openSidebarTabsAtom, [...currentOpen, tabId]);
			}
			set(activeSidebarTabAtom, tabId);
		} else {
			const currentOpen = get(openSidebarTabsAtom);
			const newOpen = currentOpen.filter((id) => id !== tabId);
			set(openSidebarTabsAtom, newOpen);

			const activeTab = get(activeSidebarTabAtom);
			if (activeTab === tabId) {
				if (newOpen.length > 0) {
					set(activeSidebarTabAtom, newOpen[newOpen.length - 1]);
				} else {
					set(activeSidebarTabAtom, "none");
				}
			}
		}
	},
);

export const sidebarWidthAtom = atomWithStorage("sidebarWidth", 300);

export const outlineJumpActionAtom = atom<{ id: string; ts: number } | null>(
	null,
);

export const outlineScrollToCurrentAtom = atom(0);
