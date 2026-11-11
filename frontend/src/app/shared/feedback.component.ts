import { Component, Input } from '@angular/core';
/** Announce persistent feedback without replacing the page or moving focus. */
@Component({ selector: 'app-feedback', standalone: true, template: `@if (message) { <p [class.error]="error" [attr.role]="error ? 'alert' : 'status'" [attr.aria-live]="error ? 'assertive' : 'polite'">{{ message }}</p> }` })
export class FeedbackComponent { @Input() message = ''; @Input() error = false; }
