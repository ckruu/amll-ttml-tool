import { type LyricLine, newLyricLine, newLyricWord } from "$/types/ttml";

interface ParsedEvent {
	time: number;
	text: string;
	index: number;
}

export function parseLrc(lrcContent: string): LyricLine[] {
	const lines = lrcContent.split(/\r?\n/);
	const parsedEvents: ParsedEvent[] = [];

	const timeTagRegex = /\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]/g;

	let globalIndex = 0;

	for (const line of lines) {
		const text = line.replace(timeTagRegex, "").trim();
		const matches = line.matchAll(timeTagRegex);

		for (const match of matches) {
			const minutes = parseInt(match[1], 10);
			const seconds = parseInt(match[2], 10);
			const fractionStr = match[3] ? `0.${match[3]}` : "0";
			const fraction = parseFloat(fractionStr);

			const totalSeconds = minutes * 60 + seconds + fraction;
			const totalMilliseconds = Math.round(totalSeconds * 1000);

			parsedEvents.push({
				time: totalMilliseconds,
				text: text,
				index: globalIndex++,
			});
		}
	}

	parsedEvents.sort((a, b) => a.time - b.time || a.index - b.index);

	const validLyricLines: LyricLine[] = [];

	let i = 0;
	while (i < parsedEvents.length) {
		const currentTime = parsedEvents[i].time;

		const group: ParsedEvent[] = [];
		while (i < parsedEvents.length && parsedEvents[i].time === currentTime) {
			group.push(parsedEvents[i]);
			i++;
		}

		const nextEvent = i < parsedEvents.length ? parsedEvents[i] : null;
		const endTime = nextEvent ? nextEvent.time : currentTime + 10000;

		const textEvents = group.filter((e) => e.text.length > 0);

		if (textEvents.length === 0) {
			continue;
		}

		const mainEvent = textEvents[0];
		const mainLine = newLyricLine();
		const mainWord = newLyricWord();

		mainWord.word = mainEvent.text;
		mainWord.startTime = currentTime;
		mainWord.endTime = endTime;

		mainLine.words = [mainWord];
		mainLine.startTime = currentTime;
		mainLine.endTime = endTime;

		if (textEvents.length > 1) {
			mainLine.translatedLyric = textEvents[1].text;
		}

		if (textEvents.length > 2) {
			mainLine.romanLyric = textEvents[2].text;
		}

		validLyricLines.push(mainLine);

		if (textEvents.length > 3) {
			for (let k = 3; k < textEvents.length; k++) {
				const extraEvent = textEvents[k];
				const extraLine = newLyricLine();
				const extraWord = newLyricWord();

				extraWord.word = extraEvent.text;
				extraWord.startTime = currentTime;
				extraWord.endTime = endTime;

				extraLine.words = [extraWord];
				extraLine.startTime = currentTime;
				extraLine.endTime = endTime;

				validLyricLines.push(extraLine);
			}
		}
	}

	return validLyricLines;
}
