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

import { Box, ContextMenu, Flex, Text } from "@radix-ui/themes";
import { atom, useAtomValue, useSetAtom, useStore } from "jotai";
import { selectAtom, splitAtom } from "jotai/utils";
import { useSetImmerAtom } from "jotai-immer";
import { focusAtom } from "jotai-optics";
import {
	type FC,
	forwardRef,
	useCallback,
	useEffect,
	useImperativeHandle,
	useMemo,
	useRef,
} from "react";
import { useTranslation } from "react-i18next";
import { toast } from "react-toastify";
import { ViewportList, type ViewportListRef } from "react-viewport-list";
import { useFileOpener } from "$/hooks/useFileOpener.ts";
import { audioEngine } from "$/modules/audio/audio-engine.ts";
import { useLyricListDrag } from "$/modules/lyric-drag/useLyricListDrag";
import {
	followActiveLineIndexAtom,
	isDraggingGlobalAtom,
	locateActionAtom,
	lyricAutoFollowAtom,
	lyricAutoFollowSmoothAtom,
	lyricLinesAtom,
	selectedLinesAtom,
	ToolMode,
	toolModeAtom,
} from "$/states/main.ts";
import { outlineJumpActionAtom } from "$/states/sidebar.ts";
import { newLyricLine } from "$/types/ttml.ts";
import styles from "./index.module.css";
import { LyricLineView } from "./lyric-line-view";

const lyricLinesOnlyAtom = splitAtom(
	focusAtom(lyricLinesAtom, (o) => o.prop("lyricLines")),
);

const lyricLineIdsAtom = selectAtom(
	lyricLinesAtom,
	(state) => state.lyricLines.map((line) => line.id),
	(prev, next) =>
		prev.length === next.length && prev.every((id, i) => id === next[i]),
);

const lyricIdToIndexMapAtom = selectAtom(lyricLineIdsAtom, (ids) => {
	const map = new Map<string, number>();
	ids.forEach((id, index) => {
		map.set(id, index);
	});
	return map;
});

import { findCurrentLineIndex } from "$/modules/lyric-editor/utils/lyric-states.ts";

export const LyricLinesView: FC = forwardRef<HTMLDivElement>((_props, ref) => {
	const store = useStore();
	const editLyric = useAtomValue(lyricLinesOnlyAtom);
	const editLyricLines = useSetImmerAtom(lyricLinesAtom);
	const setSelectedLines = useSetAtom(selectedLinesAtom);
	const toolMode = useAtomValue(toolModeAtom);
	const locateAction = useAtomValue(locateActionAtom);
	const jumpAction = useAtomValue(outlineJumpActionAtom);
	const lyricAutoFollow = useAtomValue(lyricAutoFollowAtom);
	const lyricAutoFollowSmooth = useAtomValue(lyricAutoFollowSmoothAtom);
	const setFollowActiveLineIndex = useSetAtom(followActiveLineIndexAtom);

	const { t } = useTranslation();
	const { openFile } = useFileOpener();

	const viewRef = useRef<ViewportListRef>(null);
	const viewElRef = useRef<HTMLDivElement>(null);
	const lastHandledLocateRef = useRef(locateAction);
	const lastHandledJumpRef = useRef<number | null>(null);
	const lastFollowedIndexRef = useRef(-1);

	const handlePasteTTML = useCallback(async () => {
		try {
			const text = await navigator.clipboard.readText();
			if (!text) return;
			const file = new File([text], "lyric.ttml", {
				type: "application/xml",
			});
			openFile(file, "ttml");
		} catch {
			toast.error(t("error.pasteClipboardFailed", "读取剪贴板失败"));
		}
	}, [openFile, t]);

	const handleNewLine = useCallback(() => {
		editLyricLines((state) => {
			state.lyricLines.push(newLyricLine());
		});
	}, [editLyricLines]);

	const scrollToIndexAtom = useMemo(
		() =>
			atom((get) => {
				if (toolMode !== ToolMode.Sync) return;
				const selectedLines = get(selectedLinesAtom);
				if (selectedLines.size === 0) return;
				const idToIndexMap = get(lyricIdToIndexMapAtom);
				for (const id of selectedLines) {
					const index = idToIndexMap.get(id);
					if (index !== undefined) {
						return index;
					}
				}
				return;
			}),
		[toolMode],
	);
	const scrollToIndex = useAtomValue(scrollToIndexAtom);

	const scrollToLineIndex = useCallback((index: number) => {
		const viewEl = viewElRef.current;
		if (!viewEl) return;
		const viewContainerEl = viewEl.parentElement;
		if (!viewContainerEl) return;
		viewRef.current?.scrollToIndex({
			index,
			offset: viewContainerEl.clientHeight / -2 + 50,
		});
	}, []);

	/**
	 * Scroll the follow-active line to the exact vertical center of the
	 * viewport, measured from the rendered row so lines of any height land
	 * dead-center. Falls back to the list jump (then re-measures, bounded)
	 * when the row isn't rendered yet, e.g. after a far seek.
	 */
	const scrollActiveLineToCenter = useCallback(
		(index: number, smooth: boolean, retries = 2) => {
			const viewEl = viewElRef.current;
			// NOTE: viewEl itself is the scroll container (overflowY: auto);
			// its parent does not scroll. Rows are positioned relative to
			// viewEl (position: relative), so offsetTop is already in the
			// scroller's coordinate space.
			if (!viewEl) return;
			const row = viewEl.querySelector(
				`[data-absolute-index="${index}"]`,
			) as HTMLElement | null;
			if (row) {
				const target =
					row.offsetTop - viewEl.clientHeight / 2 + row.clientHeight / 2;
				viewEl.scrollTo({
					top: Math.max(0, target),
					behavior: smooth ? "smooth" : "auto",
				});
				return;
			}
			if (retries <= 0) return;
			viewRef.current?.scrollToIndex({
				index,
				offset: viewEl.clientHeight / -2,
			});
			requestAnimationFrame(() =>
				requestAnimationFrame(() =>
					scrollActiveLineToCenter(index, false, retries - 1),
				),
			);
		},
		[],
	);

	const handleLocate = useCallback(() => {
		const lines = store.get(lyricLinesAtom).lyricLines;
		const currentTime = audioEngine.musicCurrentTime * 1000;
		const index = findCurrentLineIndex(lines, currentTime);
		if (index === -1) return;
		scrollToLineIndex(index);
		const targetLine = lines[index];
		if (targetLine) {
			setSelectedLines(new Set([targetLine.id]));
		}
	}, [store, scrollToLineIndex, setSelectedLines]);

	useEffect(() => {
		if (scrollToIndex === undefined) return;
		scrollToLineIndex(scrollToIndex);
	}, [scrollToIndex, scrollToLineIndex]);

	useEffect(() => {
		if (locateAction > 0 && locateAction !== lastHandledLocateRef.current) {
			lastHandledLocateRef.current = locateAction;
			handleLocate();
		}
	}, [locateAction, handleLocate]);

	useEffect(() => {
		if (!jumpAction || jumpAction.ts === lastHandledJumpRef.current) return;
		lastHandledJumpRef.current = jumpAction.ts;
		const idToIndexMap = store.get(lyricIdToIndexMapAtom);
		const targetIndex = idToIndexMap.get(jumpAction.id);
		if (targetIndex !== undefined && targetIndex !== -1) {
			scrollToLineIndex(targetIndex);
		}
	}, [jumpAction, store, scrollToLineIndex]);

	// Shared follow-playback: keep the currently playing line centered in
	// Edit and Sync tabs. The highlight strictly follows the line's own
	// [startTime, endTime] window: it clears in gaps (holding scroll
	// position) and reappears when the next line starts.
	useEffect(() => {
		lastFollowedIndexRef.current = -1;
		setFollowActiveLineIndex(null);
		if (!lyricAutoFollow) return;
		if (toolMode !== ToolMode.Edit && toolMode !== ToolMode.Sync) return;
		const clearHighlight = () => {
			if (lastFollowedIndexRef.current !== -2) {
				lastFollowedIndexRef.current = -2;
				setFollowActiveLineIndex(null);
			}
		};
		const onTick = () => {
			if (store.get(isDraggingGlobalAtom)) return;
			const lines = store.get(lyricLinesAtom).lyricLines;
			const now = audioEngine.musicCurrentTime * 1000;
			const index = findCurrentLineIndex(lines, now);
			if (index === -1) {
				clearHighlight();
				return;
			}
			const line = lines[index];
			const covering =
				line != null &&
				line.endTime > line.startTime &&
				now >= line.startTime &&
				now <= line.endTime;
			if (!covering) {
				clearHighlight();
				return;
			}
			if (index === lastFollowedIndexRef.current) return;
			lastFollowedIndexRef.current = index;
			setFollowActiveLineIndex(index);
			scrollActiveLineToCenter(index, lyricAutoFollowSmooth);
		};
		audioEngine.onTimeUpdate(onTick);
		return () => {
			audioEngine.offTimeUpdate(onTick);
			lastFollowedIndexRef.current = -1;
			setFollowActiveLineIndex(null);
		};
	}, [
		lyricAutoFollow,
		lyricAutoFollowSmooth,
		toolMode,
		store,
		scrollActiveLineToCenter,
		setFollowActiveLineIndex,
	]);

	const { onPointerDown } = useLyricListDrag({
		containerRef: viewElRef,
		source: "main",
		disableDrag: toolMode !== ToolMode.Edit,
	});

	useImperativeHandle(ref, () => viewElRef.current as HTMLDivElement, []);

	const innerView =
		editLyric.length === 0 ? (
			<Flex
				flexGrow="1"
				gap="2"
				align="center"
				justify="center"
				direction="column"
				height="100%"
				style={{ width: "100%", height: "100%" }}
				ref={ref}
			>
				<Text color="gray">{t("app.empty.title", "没有歌词行")}</Text>
				<Text color="gray">
					{t(
						"app.empty.description",
						"在顶部面板中添加新歌词行或从菜单栏打开 / 导入已有歌词",
					)}
				</Text>
			</Flex>
		) : (
			<Box
				flexGrow="1"
				className={styles.lyricLinesWrapper}
				height="100%"
				style={{ width: "100%", height: "100%" }}
			>
				<Box
					flexGrow="1"
					style={{
						padding: toolMode === ToolMode.Sync ? "20vh 0" : undefined,
						maxHeight: "100%",
						overflowY: "auto",
						position: "relative",
						scrollBehavior:
							lyricAutoFollow && lyricAutoFollowSmooth ? "smooth" : "auto",
					}}
					ref={viewElRef}
				>
					<div className={styles.dropIndicator} />

					<ViewportList
						overscan={10}
						items={editLyric}
						ref={viewRef}
						viewportRef={viewElRef}
					>
						{(lineAtom, i) => (
							<LyricLineView
								key={`${lineAtom}`}
								lineAtom={lineAtom}
								lineIndex={i}
								onPointerDown={onPointerDown}
							/>
						)}
					</ViewportList>
				</Box>
			</Box>
		);

	return (
		<ContextMenu.Root>
			<ContextMenu.Trigger
				style={{
					display: "flex",
					flexDirection: "column",
					flexGrow: 1,
					width: "100%",
					height: "100%",
					minHeight: 0,
				}}
			>
				{innerView}
			</ContextMenu.Trigger>
			<ContextMenu.Content>
				<ContextMenu.Item onSelect={handlePasteTTML}>
					{t("contextMenu.pasteTTML", "粘贴 TTML")}
				</ContextMenu.Item>
				<ContextMenu.Item onSelect={handleNewLine}>
					{t("contextMenu.newLine", "新建行")}
				</ContextMenu.Item>
			</ContextMenu.Content>
		</ContextMenu.Root>
	);
});

export default LyricLinesView;
