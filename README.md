# PatchDumper

A debugging tool to help inspect runtime patches (IL Hooks) that isn't miserably staring at a raw IL printout in the console.

Adds a 'dump_hooks' command that writes all active method IL Hooks to an assembly file (`Hooks.dll`, placed in this mod's `plugins` folder). Open this with any disassembler to view what your hooks look like expressed as actual code.