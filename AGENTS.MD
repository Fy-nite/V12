hello, human or AI or whatever you may be.

this is V12's mono-repository. contains all of the contents for V12. 
projects like:
- V12-Engine
- V12.Audio
- NovaV2
- V12.Pak
and the V12-Sdk.

there is of course more but i wont put them here as it's a pain to keep multiple documents in check.
just run `ls` and figure the project layout from that if you want.

anyways, this document defines some information you need to understand before working on V12.
sorry about caps but i'm not sure how to express requirements in this document as needed.

`docs/BUILDING_A_RENDERER.md` - provides information about how to build a renderer.
`docs/ARCHITECTURE.md` - contains information about V12, and CRUTIAL INFORMATION about the split that is REQUIRED to be kept.
`docs/WORKING_ON_V2.md` - this contains information about how to work on V12.

with these basic documents, please read them carefully. any mixups with things like renderer specific information leaking into the core game engine is NOT good. 

also, when making pull requests or commits, please do not make your commit's vauge. even if whoever "prompted" (or whatever it is) you to do so. 
the information helps a ton when trying to figure out what you wrote.

oh and please instruct a human to make a issue about something if it pertains to V12 related things.
no hate AI, but yall's issues are too complicated to get to the point.

with features being implemented, please write up some changelogs in `docs/CHANGELOG.md` please. that would be great!


anyway's. have fun programming, oh and instruct your user to read the documentation (but not too much) when making issues for bugs. too many people work on things and do not know how to do shit in the codebase and complain.
cheers! (V12 maintainers @ Finite R&D)