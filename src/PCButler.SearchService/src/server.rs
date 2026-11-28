// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
//! The named pipe \\.\pipe\PCButlerSearch. Its access list lets in SYSTEM and the one user who
//! installed the service, nobody else, and never from another PC.

use crate::ffi::*;
use crate::privacy::Privacy;
use crate::protocol::Pipe;
use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Arc;

pub const PIPE: &str = "\\\\.\\pipe\\PCButlerSearch";
/// At most this many connections at once (PC Butler uses two per drive).
const MOST: usize = 64;

/// A SID as text, S-1-5-21-..., with nothing in it that could change the access list.
fn valid_sid(sid: &str) -> bool {
    sid.starts_with("S-1-") && sid.len() < 200 && sid.chars().all(|c| c.is_ascii_digit() || c == '-' || c == 'S')
}

pub fn run(sid: &str, stop: &AtomicBool) -> Result<(), String> {
    if !valid_sid(sid) {
        return Err(format!("not a SID: {}", sid));
    }
    let sddl = wide(&format!("D:P(A;;GA;;;SY)(A;;GRGW;;;{})", sid));
    let mut descriptor: *mut c_void = std::ptr::null_mut();
    if unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.as_ptr(), SDDL_REVISION_1, &mut descriptor, std::ptr::null_mut()) } == 0 {
        return Err(format!("access list refused (error {})", last_error()));
    }
    let attributes = SECURITY_ATTRIBUTES {
        nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32, lpSecurityDescriptor: descriptor, bInheritHandle: 0,
    };
    let privacy = Privacy::for_sid(sid);
    let name = wide(PIPE);
    let open = Arc::new(AtomicUsize::new(0));
    let mut first = true;
    crate::log(&format!("listening on {} for {}", PIPE, sid));
    while !stop.load(Ordering::SeqCst) {
        let handle = unsafe {
            CreateNamedPipeW(name.as_ptr(), PIPE_ACCESS_DUPLEX | if first { FILE_FLAG_FIRST_PIPE_INSTANCE } else { 0 },
                             PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, PIPE_UNLIMITED_INSTANCES,
                             1 << 20, 4096, 0, &attributes)
        };
        let handle = Handle(handle);
        if !handle.valid() {
            let code = last_error();
            if first {
                unsafe { LocalFree(descriptor) };
                return Err(format!("the pipe could not be made (error {}): is the service running already?", code));
            }
            std::thread::sleep(std::time::Duration::from_secs(1));
            continue;
        }
        first = false;
        let connected = unsafe { ConnectNamedPipe(handle.0, std::ptr::null_mut()) } != 0 || last_error() == ERROR_PIPE_CONNECTED;
        if stop.load(Ordering::SeqCst) {
            break;
        }
        if !connected || open.load(Ordering::SeqCst) >= MOST {
            continue;
        }
        open.fetch_add(1, Ordering::SeqCst);
        let pipe = Pipe { handle };
        let privacy = privacy.clone();
        let count = open.clone();
        let started = std::thread::Builder::new().name("client".into()).spawn(move || {
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| crate::session::serve(pipe, privacy)));
            count.fetch_sub(1, Ordering::SeqCst);
        });
        if started.is_err() {
            open.fetch_sub(1, Ordering::SeqCst);
        }
    }
    unsafe { LocalFree(descriptor) };
    Ok(())
}

/// Wake the loop above from its wait for a client, so it sees that it should stop.
pub fn wake() {
    let name = wide(PIPE);
    let handle = unsafe {
        CreateFileW(name.as_ptr(), GENERIC_READ, 0, std::ptr::null(), OPEN_EXISTING, 0, std::ptr::null_mut())
    };
    drop(Handle(handle));
}
