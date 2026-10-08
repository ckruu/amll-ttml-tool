export const isEmptyInterludeLine = (line: {
	words: { word: string }[];
	startTime: number;
	endTime: number;
}): boolean => {
	if (line.endTime <= line.startTime) return false;
	if (line.words.length === 0) return true;
	return line.words.every((w) => w.word.trim().length === 0);
};
