
export enum CharType {
	Cjk,
	Latin,
	Numeric,
	Whitespace,
	Other,
}

export type HyphenatorFunc = (text: string) => string;

export interface SegmentationConfig {
	splitCJK: boolean;
	splitEnglish: boolean;
	punctuationWeight: number;
	punctuationMode: "merge" | "standalone";
	removeEmptySegments: boolean;
	ignoreList: Set<string>;
	customRules: Map<string, string[]>;
	hyphenator?: HyphenatorFunc;
}
