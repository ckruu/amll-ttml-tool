/**
 * Whether a line is an explicit empty interlude marker: no singable text
 * but a real time span.
 *
 * Such lines would otherwise "cover" the instrumental gap as a lyric group,
 * which both suppresses the player's automatic waiting-dots (gap-based) and
 * renders as blank space. They are filtered from the preview feed so the gap
 * — and the dots — show; TTML data and export are untouched.
 */
export const isEmptyInterludeLine = (line: {
	words: { word: string }[];
	startTime: number;
	endTime: number;
}): boolean => {
	if (line.endTime <= line.startTime) return false;
	if (line.words.length === 0) return true;
	return line.words.every((w) => w.word.trim().length === 0);
};
