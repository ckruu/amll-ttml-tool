import { Dismiss16Regular } from "@fluentui/react-icons";
import { Box, IconButton } from "@radix-ui/themes";
import { motion } from "framer-motion";
import type { TFunction } from "i18next";
import { useAtom, useAtomValue, useSetAtom } from "jotai";
import {
	type FC,
	useCallback,
	useEffect,
	useMemo,
	useRef,
	useState,
} from "react";
import { useTranslation } from "react-i18next";
import {
	activeSidebarTabAtom,
	closeTabAtom,
	openSidebarTabsAtom,
	type SidebarPanelType,
	sidebarWidthAtom,
} from "$/states/sidebar.ts";
import { ToolMode, toolModeAtom } from "$/states/main.ts";
import { BpmPanel } from "./BpmPanel";
import styles from "./index.module.css";
import { OutlineHeaderControls, OutlinePanel } from "./OutlinePanel";
import { PreviewHeaderControls, PreviewPanel } from "./PreviewPanel";
import { SidebarTabBar } from "./SidebarTabBar";

const MIN_WIDTH = 200;
const SNAP_CLOSE_THRESHOLD = 20;
const MAX_WIDTH = 650;

export interface SidebarTab {
	id: Exclude<SidebarPanelType, "none">;
	getTitle: (t: TFunction) => string;
	component: FC;
	/** Optional controls rendered in the sidebar top row when active */
	headerControls?: FC;
}

export const SIDEBAR_TABS: SidebarTab[] = [
	{
		id: "outline",
		getTitle: (t) => t("sidebar.outline.title", "大纲"),
		component: OutlinePanel,
		headerControls: OutlineHeaderControls,
	},
	{
		id: "bpm",
		getTitle: (t) => t("sidebar.bpm.title", "BPM"),
		component: BpmPanel,
	},
	{
		id: "preview",
		getTitle: (t) => t("sidebar.preview.title", "预览"),
		component: PreviewPanel,
		headerControls: PreviewHeaderControls,
	},
];

export const Sidebar = () => {
	const { t } = useTranslation();
	const openTabs = useAtomValue(openSidebarTabsAtom);
	const [activePanel, setActivePanel] = useAtom(activeSidebarTabAtom);
	const closeTab = useSetAtom(closeTabAtom);
	const toolMode = useAtomValue(toolModeAtom);
	// The sidebar Preview tab is redundant in Preview tool mode (which already
	// shows the full preview). Hide it there without touching persisted atoms,
	// so everything restores when leaving Preview mode.
	const previewHidden = toolMode === ToolMode.Preview;
	const effectiveActivePanel: SidebarPanelType =
		previewHidden && activePanel === "preview" ? "none" : activePanel;
	const [savedWidth, setSavedWidth] = useAtom(sidebarWidthAtom);

	const [isDragging, setIsDragging] = useState(false);
	const [tempWidth, setTempWidth] = useState(savedWidth);
	const sidebarRef = useRef<HTMLDivElement>(null);

	const visibleTabs = useMemo(
		() =>
			openTabs
				.map((id) => SIDEBAR_TABS.find((tab) => tab.id === id))
				.filter((tab): tab is SidebarTab => tab !== undefined)
				.filter((tab) => !(previewHidden && tab.id === "preview")),
		[openTabs, previewHidden],
	);

	const lastNonEmptyTabsCountRef = useRef(visibleTabs.length);
	if (visibleTabs.length > 0) {
		lastNonEmptyTabsCountRef.current = visibleTabs.length;
	}

	const isSingleTab =
		visibleTabs.length === 1 ||
		(visibleTabs.length === 0 && lastNonEmptyTabsCountRef.current === 1);

	const contentWidth = tempWidth > 0 ? tempWidth : savedWidth;

	const isOpen = effectiveActivePanel !== "none" && visibleTabs.length > 0;

	const handlePointerDown = useCallback((e: React.PointerEvent) => {
		e.preventDefault();
		e.currentTarget.setPointerCapture(e.pointerId);
		setIsDragging(true);
	}, []);

	const handlePointerMove = useCallback(
		(e: React.PointerEvent) => {
			if (!isDragging || !sidebarRef.current) return;

			const newWidth = e.clientX;
			const maxAllowedWidth = Math.min(MAX_WIDTH, window.innerWidth * 0.5);

			if (newWidth <= SNAP_CLOSE_THRESHOLD) {
				setTempWidth(0);
			} else {
				setTempWidth(Math.min(Math.max(newWidth, MIN_WIDTH), maxAllowedWidth));
			}
		},
		[isDragging],
	);

	const handlePointerUp = useCallback(
		(e: React.PointerEvent) => {
			if (!isDragging) return;
			setIsDragging(false);
			e.currentTarget.releasePointerCapture(e.pointerId);

			if (tempWidth <= SNAP_CLOSE_THRESHOLD) {
				setActivePanel("none");
				setTempWidth(savedWidth);
			} else {
				setSavedWidth(tempWidth);
			}
		},
		[isDragging, tempWidth, savedWidth, setActivePanel, setSavedWidth],
	);

	useEffect(() => {
		if (!isDragging) setTempWidth(savedWidth);
	}, [savedWidth, isDragging]);

	return (
		<div style={{ position: "relative", height: "100%", flexShrink: 0 }}>
			<motion.div
				ref={sidebarRef}
				className={styles.sidebarContainer}
				initial={false}
				animate={{
					width: isOpen ? tempWidth : 0,
					opacity: isOpen ? 1 : 0,
					borderRightWidth: isOpen ? 1 : 0,
				}}
				transition={{
					type: "tween",
					ease: [0.12, 0.84, 0.27, 0.98],
					duration: isDragging ? 0 : 0.25,
				}}
			>
				<div
					style={{
						width: contentWidth,
						minWidth: contentWidth,
						flexShrink: 0,
						display: "flex",
						flexDirection: "column",
						height: "100%",
					}}
				>
					<div className={styles.header} data-single-tab={isSingleTab}>
						<SidebarTabBar
							tabs={visibleTabs}
							activePanel={effectiveActivePanel}
							onSelectTab={setActivePanel}
							onCloseTab={(id) => closeTab(id)}
						/>
						{(() => {
							const ActiveControls = SIDEBAR_TABS.find(
								(tab) => tab.id === effectiveActivePanel,
							)?.headerControls;
							return ActiveControls ? (
								<div className={styles.headerControls}>
									<ActiveControls />
								</div>
							) : null;
						})()}
						{!isSingleTab && (
							<IconButton
								variant="ghost"
								color="gray"
								radius="full"
								className={styles.globalClose}
								onClick={() => setActivePanel("none")}
								aria-label={t("common.close", "关闭")}
							>
								<Dismiss16Regular />
							</IconButton>
						)}
					</div>

					<Box className={styles.content}>
						{SIDEBAR_TABS.map((tab) => {
							if (effectiveActivePanel !== tab.id) return null;
							const Comp = tab.component;
							return <Comp key={tab.id} />;
						})}
					</Box>
				</div>
			</motion.div>

			{isOpen && (
				<div
					className={styles.resizer}
					data-dragging={isDragging}
					onPointerDown={handlePointerDown}
					onPointerMove={handlePointerMove}
					onPointerUp={handlePointerUp}
					onPointerCancel={handlePointerUp}
				/>
			)}
		</div>
	);
};

export default Sidebar;
