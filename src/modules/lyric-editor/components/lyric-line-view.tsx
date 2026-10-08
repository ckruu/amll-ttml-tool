
import {
	AddFilled,
	LinkMultiple20Regular,
	TextAlignRightFilled,
	VideoBackgroundEffectFilled,
} from "@fluentui/react-icons";
import {
	Button,
	ContextMenu,
	Flex,
	IconButton,
	Text,
	TextField,
} from "@radix-ui/themes";
import { cx } from "$/utils/cx";
import { type Atom, atom, useAtomValue, useStore } from "jotai";
import { splitAtom } from "jotai/utils";
import { useSetImmerAtom } from "jotai-immer";
import {
	type FC,
	Fragment,
	memo,
	type SyntheticEvent,
	useCallback,
	useEffect,
	useLayoutEffect,
	useMemo,
	useRef,
	useState,
} from "react";
import { useTranslation } from "react-i18next";
import { LyricLineMenu } from "$/components/Menus/lyric-line-menu.tsx";
import { predictLineRomanization } from "$/modules/segmentation/utils/Transliteration/distributor";
import {
	enableAutoRomanizationPredictionAtom,
	showLineRomanizationAtom,
	showLineTranslationAtom,
	showTimestampsAtom,
	showWordRomanizationInputAtom,
	syncWrapWordsAtom,
} from "$/modules/settings/states/index.ts";
import { visualizeTimestampUpdateAtom } from "$/modules/settings/states/sync.ts";
import {
	dragSourceAtom,
	followActiveLineIndexAtom,
	isDraggingGlobalAtom,
	lyricAutoFollowAtom,
	lyricLinesAtom,
	selectedLinesAtom,
	selectedWordsAtom,
	showEndTimeAsDurationAtom,
	ToolMode,
	toolModeAtom,
} from "$/states/main.ts";
import { type LyricLine, newLyricLine, newLyricWord } from "$/types/ttml.ts";
import { msToTimestamp } from "$/utils/timestamp.ts";
import styles from "./index.module.css";
import LyricWordView from "./lyric-word-view.tsx";
import { RomanWordView } from "./roman-word-view.tsx";

const parseRubyShortcut = (value: string) => {
	if (value.endsWith("|")) {
		return {
			word: value.slice(0, -1),
			enableRuby: true,
		};
	}
	return {
		word: value,
		enableRuby: false,
	};
};

const lineDisplayNumbersAtom = atom((get) => {
	const { lyricLines } = get(lyricLinesAtom);
	const displayNumbers: number[] = [];
	let currentNumber = 0;

	for (const [index, line] of lyricLines.entries()) {
		if (!index || !line.isBG) {
			currentNumber++;
		}
		displayNumbers.push(currentNumber);
	}

	return displayNumbers;
});

const LyricLineScroller = ({
	lineAtom,
	wordsContainer,
	editingRomanWordIndex,
}: {
	lineAtom: Atom<LyricLine>;
	wordsContainer: HTMLDivElement | null;
	editingRomanWordIndex: number | null;
}) => {
	const scrollToIndexAtom = useMemo(
		() =>
			atom((get) => {
				const line = get(lineAtom);
				const selectedWords = get(selectedWordsAtom);
				if (selectedWords.size === 0) return Number.NaN;
				let scrollToIndex = Number.NaN;
				let i = 0;
				for (const word of line.words) {
					if (selectedWords.has(word.id)) {
						scrollToIndex = i;
						break;
					}
					i++;
				}
				return scrollToIndex;
			}),
		[lineAtom],
	);
	const scrollToIndex = useAtomValue(scrollToIndexAtom);

	useEffect(() => {
		const targetIndex = !Number.isNaN(scrollToIndex)
			? scrollToIndex
			: editingRomanWordIndex;
		if (targetIndex === null || Number.isNaN(targetIndex)) return;
		if (!wordsContainer) return;
		const wordEl = wordsContainer.children[targetIndex] as HTMLElement;
		if (!wordEl) return;
		wordsContainer.scrollTo({
			left: wordEl.offsetLeft - wordsContainer.clientWidth / 2,
			behavior: "auto",
		});
	}, [scrollToIndex, editingRomanWordIndex, wordsContainer]);

	useEffect(() => {
		if (!wordsContainer) return;
		const handleFocusIn = (evt: FocusEvent) => {
			const target = evt.target as HTMLElement | null;
			if (!target) return;
			const wordGroup = target.closest<HTMLElement>("[data-word-index]");
			if (!wordGroup || !wordsContainer.contains(wordGroup)) return;
			wordsContainer.scrollTo({
				left: wordGroup.offsetLeft - wordsContainer.clientWidth / 2,
				behavior: "auto",
			});
		};
		wordsContainer.addEventListener("focusin", handleFocusIn);
		return () => {
			wordsContainer.removeEventListener("focusin", handleFocusIn);
		};
	}, [wordsContainer]);

	return null;
};

const SubLineEdit = memo(
	({
		lineAtom,
		lineIndex,
		type,
	}: {
		lineAtom: Atom<LyricLine>;
		lineIndex: number;
		type: "translatedLyric" | "romanLyric";
	}) => {
		const editLyricLines = useSetImmerAtom(lyricLinesAtom);
		const line = useAtomValue(lineAtom);
		const [editing, setEditing] = useState(false);
		const [inputValue, setInputValue] = useState("");
		const { t } = useTranslation();

		const onEnter = useCallback(
			(evt: SyntheticEvent<HTMLInputElement>) => {
				setEditing(false);
				const newValue = evt.currentTarget.value;
				if (newValue !== line[type]) {
					editLyricLines((state) => {
						state.lyricLines[lineIndex][type] = newValue;
					});
				}
			},
			[editLyricLines, line, lineIndex, type],
		);

		useEffect(() => {
			if (editing) {
				setInputValue(line[type] || "");
			}
		}, [editing, line, type]);

		const label = useMemo(
			() =>
				type === "translatedLyric"
					? t("lyricLineView.translatedLabel", "翻译：")
					: t("lyricLineView.romanLabel", "音译："),
			[type, t],
		);

		return (
			<Flex align="baseline">
				<Text size="2">{label}</Text>
				{editing ? (
					<div
						className={styles.autoSizeInput}
						style={{
							maxWidth: "calc(100% - 4rem)",
							flexShrink: 1,
						}}
						onPointerDown={(e) => e.stopPropagation()}
					>
						<div
							className={styles.autoSizeInputText}
							style={{
								padding: 0,
								maxWidth: "100%",
								overflow: "hidden",
							}}
						>
							{`${inputValue}  `}
						</div>

						<TextField.Root
							className={styles.autoSizeInputField}
							autoFocus
							size="1"
							value={inputValue}
							onChange={(evt) => setInputValue(evt.currentTarget.value)}
							onBlur={onEnter}
							onKeyDown={(evt) => {
								if (evt.key === "Enter") onEnter(evt);
							}}
						/>
					</div>
				) : (
					<Button
						size="2"
						color="gray"
						variant="ghost"
						onPointerDown={(e) => e.stopPropagation()}
						onClick={(evt) => {
							evt.stopPropagation();
							setEditing(true);
						}}
						style={{
							textAlign: "left",
							maxWidth: "calc(100% - 4rem)",
							wordBreak: "break-all",
						}}
					>
						{line[type] || (
							<Text color="gray">{t("lyricLineView.empty", "无")}</Text>
						)}
					</Button>
				)}
			</Flex>
		);
	},
);

export const LyricLineView: FC<{
	lineAtom: Atom<LyricLine>;
	lineIndex: number;
	onPointerDown: (e: React.PointerEvent, lineId: string, index: number) => void;
}> = memo(({ lineAtom, lineIndex, onPointerDown }) => {
	const { t } = useTranslation();
	const line = useAtomValue(lineAtom);
	const lineSelectedAtom = useMemo(() => {
		const a = atom((get) => get(selectedLinesAtom).has(line.id));
		if (import.meta.env.DEV) {
			a.debugLabel = `lineSelectedAtom-${line.id}`;
		}
		return a;
	}, [line.id]);
	const wordsAtom = useMemo(
		() => splitAtom(atom((get) => get(lineAtom).words)),
		[lineAtom],
	);
	const words = useAtomValue(wordsAtom);
	const lineSelected = useAtomValue(lineSelectedAtom);
	const lyricAutoFollow = useAtomValue(lyricAutoFollowAtom);
	const followActiveLineIndex = useAtomValue(followActiveLineIndexAtom);
	const followActive =
		lyricAutoFollow &&
		followActiveLineIndex != null &&
		followActiveLineIndex === lineIndex;
	const editLyricLines = useSetImmerAtom(lyricLinesAtom);
	const lyricLines = useAtomValue(lyricLinesAtom);
	const visualizeTimestampUpdate = useAtomValue(visualizeTimestampUpdateAtom);
	const showTimestamps = useAtomValue(showTimestampsAtom);
	const wrapWords = useAtomValue(syncWrapWordsAtom);
	const showEndTimeAsDuration = useAtomValue(showEndTimeAsDurationAtom);
	const toolMode = useAtomValue(toolModeAtom);
	const isDragging = useAtomValue(isDraggingGlobalAtom);
	const dragSource = useAtomValue(dragSourceAtom);
	const store = useStore();
	const isDragged = isDragging && dragSource === "main" && lineSelected;
	const wordsContainerRef = useRef<HTMLDivElement>(null);

	const displayNumberAtom = useMemo(
		() => atom((get) => get(lineDisplayNumbersAtom)[lineIndex]),
		[lineIndex],
	);
	const displayNumber = useAtomValue(displayNumberAtom);

	const hasError = useMemo(() => {
		if (line.startTime > line.endTime) {
			return true;
		}
		for (const word of line.words) {
			if (word.startTime > word.endTime) {
				return true;
			}
		}
		return false;
	}, [line.startTime, line.endTime, line.words]);

	const showWordRomanizationInput = useAtomValue(showWordRomanizationInputAtom);
	const showTranslation = useAtomValue(showLineTranslationAtom);
	const showRomanization = useAtomValue(showLineRomanizationAtom);
	const editingRomanWordIndexAtom = useMemo(
		() => atom<number | null>(null),
		[],
	);
	const editingRomanWordIndex = useAtomValue(editingRomanWordIndexAtom);
	const enablePrediction = useAtomValue(enableAutoRomanizationPredictionAtom);

	const startTimeRef = useRef<HTMLDivElement>(null);
	const endTimeRef = useRef<HTMLButtonElement>(null);
	const [enableInsert, setEnableInsert] = useState(false);
	const [endTimeLinked, setEndTimeLinked] = useState(() =>
		Boolean(line.endTimeLink),
	);
	const originalEndTimeRef = useRef<number | null>(null);
	const originalNextStartTimeRef = useRef<number | null>(null);

	useEffect(() => {
		if (!visualizeTimestampUpdate) return;
		const animation = startTimeRef.current?.animate(
			[
				{
					backgroundColor:
						"color-mix(in srgb, var(--timing-start, #46a758) 80%, transparent)",
				},
				{
					backgroundColor:
						"color-mix(in srgb, var(--timing-start, #46a758) 38%, transparent)",
				},
			],
			{
				duration: 500,
			},
		);

		return () => {
			animation?.cancel();
		};
	}, [line.startTime, visualizeTimestampUpdate]);

	useLayoutEffect(() => {
		if (toolMode !== ToolMode.Edit) {
			setEnableInsert(false);
		}
	}, [toolMode]);

	useEffect(() => {
		if (!visualizeTimestampUpdate) return;
		const animation = endTimeRef.current?.animate(
			[
				{
					backgroundColor:
						"color-mix(in srgb, var(--timing-end, #e5484d) 80%, transparent)",
				},
				{
					backgroundColor:
						"color-mix(in srgb, var(--timing-end, #e5484d) 38%, transparent)",
				},
			],
			{
				duration: 500,
			},
		);

		return () => {
			animation?.cancel();
		};
	}, [line.endTime, visualizeTimestampUpdate]);

	useEffect(() => {
		if (!endTimeLinked) return;
		const nextLine = lyricLines.lyricLines[lineIndex + 1];
		if (!nextLine) {
			editLyricLines((state) => {
				const targetLine = state.lyricLines[lineIndex];
				if (!targetLine) return;
				if (targetLine.endTimeLink) delete targetLine.endTimeLink;
			});
			return;
		}
		if (nextLine.startTime === line.endTime) return;
		editLyricLines((state) => {
			const targetLine = state.lyricLines[lineIndex + 1];
			if (!targetLine) return;
			targetLine.startTime = line.endTime;
		});
	}, [endTimeLinked, editLyricLines, line.endTime, lineIndex, lyricLines]);
	useEffect(() => {
		const linked = Boolean(line.endTimeLink);
		if (linked === endTimeLinked) return;
		setEndTimeLinked(linked);
	}, [endTimeLinked, line.endTimeLink]);

	const suggestedRomans = useMemo(() => {
		if (!enablePrediction) {
			return [];
		}

		return predictLineRomanization(line.words, line.romanLyric || "");
	}, [line.romanLyric, line.words, enablePrediction]);

	const onToggleEndTimeLink = useCallback(
		(evt: React.MouseEvent<HTMLButtonElement>) => {
			evt.preventDefault();
			evt.stopPropagation();
			if (endTimeLinked) {
				setEndTimeLinked(false);
				originalEndTimeRef.current = null;
				originalNextStartTimeRef.current = null;
				editLyricLines((state) => {
					const targetLine = state.lyricLines[lineIndex];
					if (!targetLine) return;
					const linkInfo = targetLine.endTimeLink;
					if (!linkInfo) return;
					if (
						typeof linkInfo.originalEndTime !== "number" ||
						!Number.isFinite(linkInfo.originalEndTime)
					) {
						delete targetLine.endTimeLink;
						return;
					}
					targetLine.endTime = linkInfo.originalEndTime;
					const nextTarget = state.lyricLines[lineIndex + 1];
					if (
						nextTarget &&
						Number.isFinite(linkInfo.originalNextStartTime ?? Number.NaN)
					) {
						nextTarget.startTime =
							linkInfo.originalNextStartTime ?? nextTarget.startTime;
					}
					delete targetLine.endTimeLink;
				});
				return;
			}
			const nextLine = lyricLines.lyricLines[lineIndex + 1];
			if (!nextLine) return;
			originalEndTimeRef.current = line.endTime;
			originalNextStartTimeRef.current = nextLine?.startTime ?? null;
			editLyricLines((state) => {
				const targetLine = state.lyricLines[lineIndex];
				if (!targetLine) return;
				const nextTarget = state.lyricLines[lineIndex + 1];
				if (!nextTarget) return;
				const originalEndTime =
					targetLine.endTimeLink?.originalEndTime ?? targetLine.endTime;
				const originalNextStartTime =
					targetLine.endTimeLink?.originalNextStartTime ??
					nextTarget.startTime ??
					null;
				const desiredEndTime = nextTarget.startTime ?? targetLine.endTime;
				targetLine.endTimeLink = {
					originalEndTime,
					originalNextStartTime,
				};
				targetLine.endTime = desiredEndTime;
				nextTarget.startTime = desiredEndTime;
			});
			setEndTimeLinked(true);
		},
		[editLyricLines, endTimeLinked, line.endTime, lineIndex, lyricLines],
	);

	return (
		<>
			<LyricLineScroller
				lineAtom={lineAtom}
				wordsContainer={wordsContainerRef.current}
				editingRomanWordIndex={editingRomanWordIndex}
			/>
			{enableInsert && (
				<Button
					mx="2"
					my="1"
					variant="soft"
					size="1"
					style={{
						width: "calc(100% - var(--space-4))",
					}}
					onClick={() => {
						editLyricLines((state) => {
							state.lyricLines.splice(lineIndex, 0, newLyricLine());
						});
						setEnableInsert(false);
					}}
				>
					{t("lyricLineView.insertLine", "在此插入新行")}
				</Button>
			)}
			<ContextMenu.Root
				onOpenChange={(opened) => {
					if (opened) {
						if (!store.get(selectedLinesAtom).has(line.id)) {
							store.set(selectedLinesAtom, new Set([line.id]));
						}
					}
				}}
			>
				<ContextMenu.Trigger disabled={toolMode !== ToolMode.Edit}>
					<Flex
						mx="2"
						my="1"
						direction="row"
						className={cx(
							styles.lyricLine,
							lineSelected && styles.selected,
							followActive && styles.followActive,
							toolMode === ToolMode.Sync && styles.sync,
							toolMode === ToolMode.Edit && styles.edit,
							line.ignoreSync && styles.ignoreSync,
							hasError && toolMode === ToolMode.Edit && styles.error,
						)}
						align="center"
						gapX="4"
						data-line-id={line.id}
						data-absolute-index={lineIndex}
						data-is-dragged={isDragged}
						onPointerDown={(evt) => {
							onPointerDown(evt, line.id, lineIndex);
						}}
						asChild
					>
						<div>
							<Flex direction="column" align="center" justify="center" ml="3">
								<Text
									className={cx(
										styles.lineNumber,
										line.ignoreSync && styles.ignored,
									)}
									align="center"
									color="gray"
								>
									{displayNumber}
								</Text>
								{line.isBG && <VideoBackgroundEffectFilled color="#4466FF" />}
								{line.isDuet && <TextAlignRightFilled color="#44AA33" />}
							</Flex>
							<div
								className={cx(
									styles.lyricLineContainer,
									toolMode === ToolMode.Edit && styles.edit,
									toolMode === ToolMode.Sync && styles.sync,
								)}
							>
								<div
									className={cx(
										styles.lyricWordsContainer,
										toolMode === ToolMode.Edit && styles.edit,
										toolMode === ToolMode.Sync && styles.sync,
										toolMode === ToolMode.Sync && wrapWords && styles.wrap,
										!showTimestamps && styles.hideTimestamps,
									)}
									ref={wordsContainerRef}
								>
									{words.map((wordAtom, wi) => {
										const word = store.get(wordAtom);
										return (
											<Fragment key={`word-${word.id}`}>
												{enableInsert && (
													<IconButton
														size="1"
														variant="soft"
														onClick={(evt) => {
															evt.preventDefault();
															evt.stopPropagation();
															editLyricLines((state) => {
																state.lyricLines[lineIndex].words.splice(
																	wi,
																	0,
																	newLyricWord(),
																);
															});
														}}
													>
														<AddFilled />
													</IconButton>
												)}
												<Flex
													direction="column"
													align="stretch"
													gap="3"
													data-word-index={wi}
													className={styles.wordGroup}
													onPointerDown={(e) => e.stopPropagation()}
												>
													<LyricWordView
														wordAtom={wordAtom}
														wordIndex={wi}
														line={line}
														lineIndex={lineIndex}
													/>
													{toolMode === ToolMode.Edit &&
														showWordRomanizationInput && (
															<RomanWordView
																wordAtom={wordAtom}
																wordIndex={wi}
																editingIndexAtom={editingRomanWordIndexAtom}
																suggestedRoman={suggestedRomans[wi]}
															/>
														)}
												</Flex>
											</Fragment>
										);
									})}
									{enableInsert && (
										<IconButton
											size="1"
											variant="soft"
											onClick={(evt) => {
												evt.preventDefault();
												evt.stopPropagation();
												editLyricLines((state) => {
													state.lyricLines[lineIndex].words.push(
														newLyricWord(),
													);
												});
											}}
										>
											<AddFilled />
										</IconButton>
									)}
									{toolMode === ToolMode.Edit && (
										<TextField.Root
											placeholder={t("lyricLineView.insertWord", "插入单词…")}
											className={cx(
												styles.insertWordField,
												words.length === 0 && styles.empty,
											)}
											style={{
												alignSelf: "center",
											}}
											onKeyDown={(evt) => {
												if (evt.key === "Enter") {
													evt.preventDefault();
													evt.stopPropagation();
													const { word, enableRuby } = parseRubyShortcut(
														evt.currentTarget.value,
													);
													editLyricLines((state) => {
														const newWord = newLyricWord();
														state.lyricLines[lineIndex].words.push({
															...newWord,
															word,
															ruby: enableRuby
																? [
																		{
																			word: "",
																			startTime: newWord.startTime,
																			endTime: newWord.endTime,
																		},
																	]
																: undefined,
														});
													});
													evt.currentTarget.value = "";
												}
											}}
											onPointerDown={(e) => e.stopPropagation()}
										/>
									)}
								</div>
								{toolMode === ToolMode.Edit && (
									<>
										{showTranslation && (
											<SubLineEdit
												lineAtom={lineAtom}
												lineIndex={lineIndex}
												type="translatedLyric"
											/>
										)}
										{showRomanization && (
											<SubLineEdit
												lineAtom={lineAtom}
												lineIndex={lineIndex}
												type="romanLyric"
											/>
										)}
									</>
								)}
							</div>
							{toolMode === ToolMode.Edit && (
								<Flex p="3">
									<IconButton
										size="1"
										variant={enableInsert ? "solid" : "soft"}
										onClick={(evt) => {
											evt.preventDefault();
											evt.stopPropagation();
											setEnableInsert((v) => !v);
										}}
									>
										<AddFilled />
									</IconButton>
								</Flex>
							)}
							{toolMode === ToolMode.Sync && showTimestamps && (
								<Flex pr="3" gap="1" direction="column" align="stretch">
									<div className={styles.startTime} ref={startTimeRef}>
										{msToTimestamp(line.startTime)}
									</div>
									<button
										type="button"
										className={cx(styles.endTime, styles.endTimeButton)}
										ref={endTimeRef}
										onClick={onToggleEndTimeLink}
									>
										<span
											style={{
												display: "inline-flex",
												alignItems: "center",
											}}
										>
											{endTimeLinked ? (
												<LinkMultiple20Regular />
											) : showEndTimeAsDuration ? (
												`+${line.endTime - line.startTime}ms`
											) : (
												msToTimestamp(line.endTime)
											)}
										</span>
									</button>
								</Flex>
							)}
						</div>
					</Flex>
				</ContextMenu.Trigger>
				<ContextMenu.Content>
					<LyricLineMenu lineIndex={lineIndex} />
				</ContextMenu.Content>
			</ContextMenu.Root>
			{enableInsert && (
				<Button
					mx="2"
					my="1"
					variant="soft"
					size="1"
					style={{
						width: "calc(100% - var(--space-4))",
					}}
					onClick={() => {
						editLyricLines((state) => {
							state.lyricLines.splice(lineIndex + 1, 0, newLyricLine());
						});
						setEnableInsert(false);
					}}
				>
					{t("lyricLineView.insertLine", "在此插入新行")}
				</Button>
			)}
		</>
	);
});
