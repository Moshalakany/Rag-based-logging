import { bootstrapApplication } from '@angular/platform-browser';
import { AppComponent } from './app/app.component';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { MARKED_OPTIONS, provideMarkdown } from 'ngx-markdown';

bootstrapApplication(AppComponent, {
  providers: [
    provideHttpClient(),
    provideRouter([]),
    provideMarkdown({
      markedOptions: {
        provide: MARKED_OPTIONS,
        useValue: { breaks: true, gfm: true, pedantic: false },
      },
    }),
  ],
}).catch((err) => console.error(err));
