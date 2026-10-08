
export type GetDetail<E> = E extends CustomEvent<infer D> ? D : never;

export type TypedEventListener<M, T extends keyof M> = (
	evt: M[T],
) => void | Promise<void>;

export interface TypedEventListenerObject<M, T extends keyof M> {
	handleEvent: (evt: M[T]) => void | Promise<void>;
}

export type TypedEventListenerOrEventListenerObject<M, T extends keyof M> =
	| TypedEventListener<M, T>
	| TypedEventListenerObject<M, T>;

type ValueIsEvent<T> = {
	[key in keyof T]: Event;
};

export class TypedEventTarget<M extends ValueIsEvent<M>> extends EventTarget {
	override addEventListener<T extends keyof M & string>(
		type: T,
		listener: TypedEventListenerOrEventListenerObject<M, T> | null,
		options?: boolean | AddEventListenerOptions,
	): void {
		super.addEventListener(
			type,
			listener as EventListenerOrEventListenerObject,
			options,
		);
	}

	override removeEventListener<T extends keyof M & string>(
		type: T,
		callback: TypedEventListenerOrEventListenerObject<M, T> | null,
		options?: EventListenerOptions | boolean,
	): void {
		super.removeEventListener(
			type,
			callback as EventListenerOrEventListenerObject,
			options,
		);
	}

	override dispatchEvent(event: Event): boolean {
		return super.dispatchEvent(event);
	}

	public dispatchTypedEvent<T extends keyof M>(_type: T, event: M[T]): boolean {
		return super.dispatchEvent(event);
	}

	public dispatch<T extends keyof M & string>(
		type: T,
		...args: GetDetail<M[T]> extends undefined
			? [detail?: GetDetail<M[T]>]
			: [detail: GetDetail<M[T]>]
	): boolean {
		const init: CustomEventInit = args.length > 0 ? { detail: args[0] } : {};

		const event = new CustomEvent(type, init);

		return this.dispatchTypedEvent(type, event as Event as M[T]);
	}
}
