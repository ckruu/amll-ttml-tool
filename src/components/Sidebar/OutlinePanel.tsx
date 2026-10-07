import { Button, ContextMenu, Flex, Text } from "@radix-ui/themes";
import { useAtomValue, useSetAtom, useStore } from "jotai";
import { memo, useCallback, useEffect, useMemo, useRef, type FC } from "react";
import { useTranslation } from "react-i18next";
import { LyricLineMenu } from "$/components/Menus/lyric-line-menu";
import { audioEngine } from "$/modules/audio/audio-engine";
import { useLyricListDrag } from "$/modules/lyric-drag/useLyricListDrag";
import { findCurrentLineIndex } from "$/modules/lyric-editor/utils/lyric-states";
import {
	dragSourceAtom,
	isDraggingGlobalAtom,
	lyricLinesAtom,
	selectedLinesAtom,
	ToolMode,
	toolModeAtom,
} from "$/states/main.ts";
import { outlineJumpActionAtom, outlineScrollToCurrentAtom } from "$/states/sidebar.ts";
import styles from "./OutlinePanel.module.css";

/**
 * Outline tab header controls (rendered in the sidebar top row):
 * line count + jump-to-current button.
 */
export const OutlineHeaderControls: FC = () => {
	const { t } = useTranslation();
	const { lyricLines } = useAtomValue(lyricLinesAtom);
	const triggerScroll = useSetAtom(outlineScrollToCurrentAtom);
	return (
		<Flex align="center" gap="1" flexShrink="0">
			<Text size="1" color="gray">
				{t("sidebar.outline.count", "{count} lines", {
					count: lyricLines.length,
				})}
			</Text>
			<Button
				size="1"
				variant="ghost"
				color="gray"
				onClick={() => triggerScroll((n) => n + 1)}
			>
				{t("sidebar.outline.jumpToCurrent", "Current")}
			</Button>
		</Flex>
	);
};

export const OutlinePanel = memo(() => {
	const { t } = useTranslation();
	const store = useStore();

	const { lyricLines } = useAtomValue(lyricLinesAtom);
	const selectedLines = useAtomValue(selectedLinesAtom);
	const toolMode = useAtomValue(toolModeAtom);
	const isDragging = useAtomValue(isDraggingGlobalAtom);
	const dragSource = useAtomValue(dragSourceAtom);

	const setSelectedLines = useSetAtom(selectedLinesAtom);
	const setJumpAction = useSetAtom(outlineJumpActionAtom);

	const containerRef = useRef<HTMLDivElement>(null);
	const scrollToCurrent = useAtomValue(outlineScrollToCurrentAtom);
	const lastHandledScrollRef = useRef(0);

	const { onPointerDown } = useLyricListDrag({
		containerRef,
		source: "outline",
	});

	const displayNumbers = useMemo(() => {
		const nums: number[] = [];
		let currentNumber = 0;
		for (let i = 0; i < lyricLines.length; i++) {
			if (!i || !lyricLines[i].isBG) currentNumber++;
			nums.push(currentNumber);
		}
		return nums;
	}, [lyricLines]);

	const handleItemDoubleClick = useCallback(
		(lineId: string) => {
			setSelectedLines(new Set([lineId]));
			setJumpAction({ id: lineId, ts: Date.now() });
		},
		[setSelectedLines, setJumpAction],
	);

	const handleContextMenuOpen = useCallback(
		(opened: boolean, lineId: string) => {
			if (opened && !isDragging) {
				const currentSelected = store.get(selectedLinesAtom);
				if (!currentSelected.has(lineId)) {
					store.set(selectedLinesAtom, new Set([lineId]));
				}
			}
		},
		[store, isDragging],
	);

	useEffect(() => {
		if (scrollToCurrent === 0 || scrollToCurrent === lastHandledScrollRef.current) {
			return;
		}
		lastHandledScrollRef.current = scrollToCurrent;
		const index = findCurrentLineIndex(
			lyricLines,
			audioEngine.musicCurrentTime * 1000,
		);
		if (index < 0) return;
		const el = containerRef.current?.querySelector(
			`[data-absolute-index="${index}"]`,
		);
		el?.scrollIntoView({ block: "center", behavior: "smooth" });
	}, [scrollToCurrent, lyricLines]);

	return (
		<div
			className={`${styles.outlineContainer} ${isDragging ? styles.isDraggingGlobal : ""}`}
			ref={containerRef}
			style={{
				position: "relative",
				flexGrow: 1,
				minHeight: 0,
				height: "auto",
			}}
		>
			<div className={styles.dropIndicator} />

			{lyricLines.map((line, index) => {
				const isSelected = selectedLines.has(line.id);
				const isDragged = isDragging && dragSource === "outline" && isSelected;
				const textContent = line.words.map((w) => w.word).join("");
				const hasText = textContent.trim().length > 0;

				return (
					<div
						key={line.id}
						className={styles.itemWrapper}
						data-line-id={line.id}
						data-absolute-index={index}
					>
						<ContextMenu.Root
							onOpenChange={(opened) => handleContextMenuOpen(opened, line.id)}
						>
							<ContextMenu.Trigger disabled={toolMode !== ToolMode.Edit}>
								<div
									className={styles.outlineItem}
									data-selected={isSelected}
									data-is-dragged={isDragged}
									onPointerDown={(e) => {
										onPointerDown(e, line.id, index);
									}}
									onDoubleClick={() => handleItemDoubleClick(line.id)}
								>
									<div className={styles.lineNumber}>
										{displayNumbers[index]}
									</div>

									<div
										className={`
										${styles.textContent} 
										${line.isBG ? styles.isBG : ""} 
										${line.isDuet ? styles.isDuet : ""}
									`}
									>
										<div className={styles.primaryText}>
											{hasText ? (
												textContent
											) : (
												<span className={styles.emptyText}>
													{t("sidebar.outline.emptyLine", "(空行)")}
												</span>
											)}
										</div>
									</div>
								</div>
							</ContextMenu.Trigger>

							<ContextMenu.Content>
								<LyricLineMenu lineIndex={index} />
							</ContextMenu.Content>
						</ContextMenu.Root>
					</div>
				);
			})}
			</div>
	);
});
