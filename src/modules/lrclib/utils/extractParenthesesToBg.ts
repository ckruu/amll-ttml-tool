import { uid } from "uid";
import { type LyricLine, newLyricWord } from "$/types/ttml";

const capitalizeFirstLetter = (str: string) => {
	if (!str) return "";
	return str.charAt(0).toUpperCase() + str.slice(1);
};

export function extractParenthesesToBg(line: LyricLine): LyricLine[] {
	const fullText = line.words.map((w) => w.word).join("");
	const regex = /([(（])(.*?)([)）])/g;
	const matches = Array.from(fullText.matchAll(regex));

	if (matches.length === 0) {
		return [line];
	}

	const rawMainText = fullText.replace(regex, "").replace(/\s+/g, " ").trim();
	const mainText = capitalizeFirstLetter(rawMainText);

	const rawBgText = matches.map((m) => m[2].trim()).join(" ");
	const bgText = capitalizeFirstLetter(rawBgText);

	if (!mainText) {
		return [
			{ ...line, isBG: true, words: [{ ...line.words[0], word: bgText }] },
		];
	}

	const totalDuration = line.endTime - line.startTime;
	const mainLen = mainText.length;
	const bgLen = bgText.length;
	const totalLen = mainLen + bgLen;

	if (totalLen === 0) return [line];

	const mainDuration = Math.floor(totalDuration * (mainLen / totalLen));

	const mainLine: LyricLine = {
		...line,
		words: [
			{
				...newLyricWord(),
				word: mainText,
				startTime: line.startTime,
				endTime: line.startTime + mainDuration,
			},
		],
		endTime: line.startTime + mainDuration,
	};

	const bgLine: LyricLine = {
		...line,
		id: uid(),
		isBG: true,
		words: [
			{
				...newLyricWord(),
				word: bgText,
				startTime: line.startTime + mainDuration,
				endTime: line.endTime,
			},
		],
		startTime: line.startTime + mainDuration,
	};

	return [mainLine, bgLine];
}
