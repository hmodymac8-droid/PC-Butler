// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
//! PCButlerSearch.exe, PC Butler's search service.
//!
//!   PCButlerSearch.exe --service --user <SID>   run as the Windows service (how Windows starts it)
//!   PCButlerSearch.exe --console [--user <SID>]  the same in this window, for debugging (as administrator)
//!
//! It only reads: it opens drives for reading, never writes a file, never starts a program.
//! It is installed by PC Butler's administrator helper into %ProgramFiles%\PC Butler Search.
#![windows_subsystem = "console"]

mod ffi;
mod mft;
mod privacy;
mod protocol;
mod server;
mod service;
mod session;
mod snapshot;
mod volume;

use std::sync::atomic::{AtomicBool, Ordering};

static CONSOLE: AtomicBool = AtomicBool::new(false);
static STOP: AtomicBool = AtomicBool::new(false);

/// A line for whoever watches the console; a service has nobody to tell, and writes nothing.
pub fn log(text: &str) {
    if CONSOLE.load(Ordering::Relaxed) {
        eprintln!("{}", text);
    }
}

fn user(arguments: &[String]) -> Option<String> {
    arguments.iter().position(|argument| argument == "--user").and_then(|at| arguments.get(at + 1)).cloned()
}

fn main() {
    let arguments: Vec<String> = std::env::args().collect();
    let code = match arguments.get(1).map(String::as_str) {
        Some("--console") => {
            CONSOLE.store(true, Ordering::Relaxed);
            match user(&arguments).or_else(privacy::current_sid) {
                Some(sid) => match server::run(&sid, &STOP) {
                    Ok(()) => 0,
                    Err(problem) => {
                        eprintln!("{}", problem);
                        1
                    }
                },
                None => {
                    eprintln!("Could not tell who you are; pass --user <SID>.");
                    1
                }
            }
        }
        Some("--service") => match user(&arguments) {
            Some(sid) => match service::start(sid) {
                Ok(()) => 0,
                Err(_) => 1,
            },
            None => 2,
        },
        _ => {
            eprintln!("PC Butler search service. Installed and started by PC Butler (Search settings, \"Make search instant\").");
            eprintln!("  --console [--user <SID>]   run in this window, for debugging (needs administrator rights)");
            2
        }
    };
    std::process::exit(code);
}
