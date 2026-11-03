# Phase 1: scope and foundation

**Window:** November 2026  
**Working pace:** about 3–5 hours per week, planned around the changing Codex Free allowance.

## Product goal

Build a straightforward Windows 11 desktop client for browsing Pixelfed and managing the user’s own photo posts. The first milestone is a native app shell that gives later API work a stable home.

## Decisions

- **Platform and UI:** C# with WinUI 3 and the Windows App SDK, using the official NavigationView template.
- **Navigation:** Feed, Bookmarks, Collections, and Settings.
- **Initial account scope:** one Pixelfed account on one chosen instance. Keep the instance URL configurable so the design can grow to multiple accounts later.
- **Collections:** use Pixelfed’s server-side collections for posts the signed-in user owns. The API’s add endpoint does not provide a way to add arbitrary posts from other accounts.
- **Create from the app:** show “Create new collection…” in the post action menu. Open `https://<instance>/i/collections/create` in an in-app web view, let the user sign in there if needed, then return to the client, refresh their collections, and add the post. Do not put the OAuth access token in a URL or inject it into page scripts.
- **Eligibility:** the Pixelfed add endpoint accepts only the signed-in user’s public or unlisted photo, album, and video posts. Hide or disable the server-collection action for other posts and explain why when needed.
- **Create flow fallback:** if the user closes the web view before finishing, leave the add action available and offer a refresh/retry. Never report a collection as created until it appears in the authenticated collection list.

## Phase 1 deliverable

- A launchable WinUI app shell with the four navigation destinations.
- Placeholder states that explain which features arrive in later phases.
- This product scope and the implementation constraints for collections.

## Six-month roadmap

1. **November 2026 — Foundation:** scope, WinUI app shell, navigation, and build setup.
2. **December — Sign-in and reading:** connect to one Pixelfed instance, authenticate, load the home feed, and show post details.
3. **January 2027 — Everyday actions:** bookmarks, likes, paging, loading states, and recoverable API errors.
4. **February — Collection menu:** add the three-dot post menu, list the user’s collections, and add an eligible own post to a selected collection.
5. **March — Create collection from the app:** implement the first-party web-view flow, refresh collections when the user returns, and add the originating post.
6. **April — Reliability and release prep:** verify behavior across instances, improve accessibility and window sizing, package a Windows build, and document limitations.

## Free-tier working rhythm

- Keep each Codex session to one small, reviewable change.
- Put the target files and acceptance criteria in each task prompt.
- Ask Codex to summarize the diff; inspect it before moving to the next feature.
- Save progress in Git after each coherent milestone so a usage reset does not lose context.
- Check the in-app usage page for the current allowance and reset time; do not plan around a fixed message count.

## Phase 1 acceptance criteria

- The app opens as a native Windows desktop window.
- The left navigation switches among Feed, Bookmarks, Collections, and Settings.
- Empty states do not imply a connected account or live Pixelfed data.
- The collection workaround and the own-post API restriction are documented before implementation.

## Phase 2 progress

- **Instance discovery implemented:** Settings accepts a server domain, requires HTTPS, checks `/api/v2/instance` and `/api/v2.1/config`, displays server title/version and OAuth availability, and remembers the selected server in Windows app-local settings.
- **Sign-in implemented:** on startup, show a connect dialog when the selected instance has no saved account. It registers an OAuth client for that instance, uses an embedded sign-in view, validates OAuth `state`, verifies the resulting account, and stores credentials in Windows Credential Locker. Feed reading uses `read`; collection changes require Pixelfed’s broader `write` scope.
- **Home timeline implemented:** Feed loads up to 20 recent statuses from the authenticated home timeline, shows account name, handle, caption, timestamp, and the first image attachment, and provides refresh and empty/error states.
- **Own profile and collection actions implemented:** My profile loads the signed-in account’s own posts. Each post menu can add it to one of the user’s collections or create a collection in Pixelfed’s web editor, then refresh and add the post.
- **Next slice:** test the collection creation workflow against your account and instance, then add collection browsing and post action polish.
