export interface JsError {
	kind: string;

	message?: string;

	byteOffset?: number;

	lineId?: string;

	tagStack?: string[];

	currentAttribute?: string;

	offendingString?: string;
}
