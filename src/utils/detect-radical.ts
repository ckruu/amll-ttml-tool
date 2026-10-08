const RADICAL_CHAR_REGEXP = /[\u2e80-\u2eff\u2f00-\u2fdf\u31c0-\u31ef]/u;

export function containsRadicalChar(text: string): boolean {
	return RADICAL_CHAR_REGEXP.test(text);
}
