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

import { Button, Checkbox, Flex, Grid, Text, TextField } from "@radix-ui/themes";
import { useAtom, useSetAtom } from "jotai";
import { forwardRef } from "react";
import { useTranslation } from "react-i18next";
import {
	hideObsceneWordsAtom,
	lyricWordFadeWidthAtom,
	previewAnimSpeedAtom,
	previewAutoContrastAtom,
	previewBgBrightnessAtom,
	previewBgContrastAtom,
	previewBgEnabledAtom,
	previewBgSaturationAtom,
	previewBgStaticAtom,
	previewBlurStrengthAtom,
	previewCascadeAtom,
	previewEmphasisAtom,
	previewGlowAtom,
	previewInactiveBlurAtom,
	previewInactiveScaleAtom,
	previewPlusLighterAtom,
	previewFullscreenAtom,
	previewWordLiftAtom,
	showRomanLinesAtom,
	showTranslationLinesAtom,
} from "$/modules/settings/states/preview";
import { RibbonFrame, RibbonSection } from "./common";
import {
	PreviewCheckRow,
	PreviewFontSizeField,
	PreviewSliderRow,
	PreviewVsyncField,
} from "./PreviewViewFields";

export const PreviewModeRibbonBar = forwardRef<HTMLDivElement>(
	(_props, ref) => {
		const [showTranslationLine, setShowTranslationLine] = useAtom(
			showTranslationLinesAtom,
		);
		const [showRomanLine, setShowRomanLine] = useAtom(showRomanLinesAtom);
		const [hideObsceneWords, setHideObsceneWords] =
			useAtom(hideObsceneWordsAtom);
		const [lyricWordFadeWidth, setLyricWordFadeWidth] = useAtom(
			lyricWordFadeWidthAtom,
		);
		const [emphasis, setEmphasis] = useAtom(previewEmphasisAtom);
		const [glow, setGlow] = useAtom(previewGlowAtom);
		const [wordLift, setWordLift] = useAtom(previewWordLiftAtom);
		const [animSpeed, setAnimSpeed] = useAtom(previewAnimSpeedAtom);
		const [inactiveScale, setInactiveScale] = useAtom(previewInactiveScaleAtom);
		const [cascade, setCascade] = useAtom(previewCascadeAtom);
		const [inactiveBlur, setInactiveBlur] = useAtom(previewInactiveBlurAtom);
		const [blurStrength, setBlurStrength] = useAtom(previewBlurStrengthAtom);
		const [plusLighter, setPlusLighter] = useAtom(previewPlusLighterAtom);
		const [bgEnabled, setBgEnabled] = useAtom(previewBgEnabledAtom);
		const [bgStatic, setBgStatic] = useAtom(previewBgStaticAtom);
		const [bgBrightness, setBgBrightness] = useAtom(previewBgBrightnessAtom);
		const [bgContrast, setBgContrast] = useAtom(previewBgContrastAtom);
		const [bgSaturation, setBgSaturation] = useAtom(previewBgSaturationAtom);
		const [autoContrast, setAutoContrast] = useAtom(previewAutoContrastAtom);
		const setPreviewFullscreen = useSetAtom(previewFullscreenAtom);
		const { t } = useTranslation();

		return (
			<RibbonFrame ref={ref}>
				<RibbonSection label={t("ribbonBar.previewMode.lyrics", "歌词")}>
					<Grid columns="0fr 0fr" gap="2" gapY="1" flexGrow="1" align="center">
						<Text wrap="nowrap" size="1">
							{t("ribbonBar.previewMode.showTranslation", "显示翻译")}
						</Text>
						<Checkbox
							checked={showTranslationLine}
							onCheckedChange={(v) => setShowTranslationLine(!!v)}
						/>
						<Text wrap="nowrap" size="1">
							{t("ribbonBar.previewMode.showRoman", "显示音译")}
						</Text>
						<Checkbox
							checked={showRomanLine}
							onCheckedChange={(v) => setShowRomanLine(!!v)}
						/>
						<Text wrap="nowrap" size="1">
							{t("ribbonBar.previewMode.maskObsceneWords", "屏蔽不雅用语")}
						</Text>
						<Checkbox
							checked={hideObsceneWords}
							onCheckedChange={(v) => setHideObsceneWords(!!v)}
						/>
					</Grid>
				</RibbonSection>
				<RibbonSection label={t("ribbonBar.previewMode.word", "单词")}>
					<Grid columns="0fr 0fr" gap="2" gapY="1" flexGrow="1" align="center">
						<Text wrap="nowrap" size="1">
							{t("ribbonBar.previewMode.fadeWidth", "过渡宽度")}
						</Text>
						<TextField.Root
							min={0}
							step={0}
							size="1"
							style={{
								width: "4em",
							}}
							defaultValue={lyricWordFadeWidth}
							onBlur={(e) => {
								const value = Number.parseFloat(e.target.value);
								if (Number.isFinite(value)) {
									setLyricWordFadeWidth(value);
								}
							}}
						/>
					</Grid>
				</RibbonSection>
				<RibbonSection label={t("ribbonBar.previewView.title", "视图")}>
					<Flex direction="column" gap="1" flexGrow="1" justify="center">
						<PreviewFontSizeField target="main" />
						<PreviewVsyncField />
						<Flex gap="3" align="center">
							<Text size="1" style={{ userSelect: "none" }}>
								{t("ribbonBar.previewView.fullscreen", "全屏预览")}
							</Text>
							<Button
								size="1"
								variant="soft"
								onClick={() => setPreviewFullscreen(true)}
							>
								{t("ribbonBar.previewView.fullscreenGo", "进入全屏")}
							</Button>
						</Flex>
					</Flex>
				</RibbonSection>
				<RibbonSection label={t("ribbonBar.previewStyle.title", "歌词样式")}>
					<Flex direction="row" gap="4" flexGrow="1" align="start" justify="center">
						<Flex direction="column" gap="1" justify="center">
							<PreviewSliderRow
								label={t("ribbonBar.previewStyle.emphasis", "强调强度")}
								value={Math.round(emphasis * 100)}
								min={0}
								max={200}
								onChange={(v) => setEmphasis(v / 100)}
								display={`${Math.round(emphasis * 100)}%`}
							/>
							<PreviewSliderRow
								label={t("ribbonBar.previewStyle.glow", "辉光强度")}
								value={Math.round(glow * 100)}
								min={0}
								max={200}
								onChange={(v) => setGlow(v / 100)}
								display={`${Math.round(glow * 100)}%`}
							/>
							<PreviewSliderRow
								label={t("ribbonBar.previewStyle.wordLift", "上浮幅度")}
								value={Math.round(wordLift * 100)}
								min={0}
								max={200}
								onChange={(v) => setWordLift(v / 100)}
								display={`${Math.round(wordLift * 100)}%`}
							/>
							<PreviewSliderRow
								label={t("ribbonBar.previewStyle.animSpeed", "动画速度")}
								value={Math.round(animSpeed * 100)}
								min={20}
								max={200}
								onChange={(v) => setAnimSpeed(v / 100)}
								display={`${Math.round(animSpeed * 100)}%`}
							/>
							<PreviewSliderRow
								label={t("ribbonBar.previewStyle.inactiveScale", "非活动行缩放")}
								value={Math.round(inactiveScale * 100)}
								min={50}
								max={100}
								onChange={(v) => setInactiveScale(v / 100)}
								display={`${Math.round(inactiveScale * 100)}%`}
							/>
						</Flex>
						<Flex direction="column" gap="1" justify="center">
							<PreviewSliderRow
								label={t("ribbonBar.previewStyle.cascade", "层叠强度")}
								value={Math.round(cascade * 100)}
								min={0}
								max={200}
								onChange={(v) => setCascade(v / 100)}
								display={`${Math.round(cascade * 100)}%`}
							/>
							<PreviewCheckRow
								label={t("ribbonBar.previewStyle.inactiveBlur", "非活动行模糊")}
								checked={inactiveBlur}
								onChange={setInactiveBlur}
							/>
							<PreviewSliderRow
								label={t("ribbonBar.previewStyle.blurStrength", "模糊强度")}
								value={Math.round(blurStrength * 100)}
								min={0}
								max={200}
								onChange={(v) => setBlurStrength(v / 100)}
								display={`${Math.round(blurStrength * 100)}%`}
							/>
							<PreviewCheckRow
								label={t("ribbonBar.previewStyle.plusLighter", "叠光混合")}
								checked={plusLighter}
								onChange={setPlusLighter}
							/>
						</Flex>
					</Flex>
				</RibbonSection>
				<RibbonSection label={t("ribbonBar.previewBg.title", "背景")}>
					<Flex direction="row" gap="4" flexGrow="1" align="start" justify="center">
						<Flex direction="column" gap="1" justify="center">
							<PreviewCheckRow
								label={t("ribbonBar.previewBg.enabled", "着色器背景")}
								checked={bgEnabled}
								onChange={setBgEnabled}
							/>
							<PreviewCheckRow
								label={t("ribbonBar.previewBg.static", "静止画面")}
								checked={bgStatic}
								onChange={setBgStatic}
							/>
							<PreviewSliderRow
								label={t("ribbonBar.previewBg.brightness", "最大亮度")}
								value={Math.round(bgBrightness * 100)}
								min={20}
								max={150}
								onChange={(v) => setBgBrightness(v / 100)}
								display={`${Math.round(bgBrightness * 100)}%`}
							/>
						</Flex>
						<Flex direction="column" gap="1" justify="center">
							<PreviewSliderRow
								label={t("ribbonBar.previewBg.contrast", "对比度")}
								value={Math.round(bgContrast * 100)}
								min={50}
								max={150}
								onChange={(v) => setBgContrast(v / 100)}
								display={`${Math.round(bgContrast * 100)}%`}
							/>
							<PreviewSliderRow
								label={t("ribbonBar.previewBg.saturation", "饱和度")}
								value={Math.round(bgSaturation * 100)}
								min={0}
								max={200}
								onChange={(v) => setBgSaturation(v / 100)}
								display={`${Math.round(bgSaturation * 100)}%`}
							/>
							<PreviewCheckRow
								label={t("ribbonBar.previewBg.autoContrast", "自动对比歌词")}
								checked={autoContrast}
								onChange={setAutoContrast}
							/>
						</Flex>
					</Flex>
				</RibbonSection>
			</RibbonFrame>
		);
	},
);

export default PreviewModeRibbonBar;
