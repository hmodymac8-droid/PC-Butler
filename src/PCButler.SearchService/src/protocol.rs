// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
//! The pipe protocol, version 1. Everything is little-endian. A frame is a u32 length (of what
//! follows it), a u8 type and the type's fields. Text is a u16 count of UTF-16 characters and
//! the characters. The C# side is PCButler.Core.Search's SearchService.cs; change both together
//! and raise VERSION when a frame changes.
//!
//! Client to service:
//!   HELLO      u32 magic "PCBS", u16 version
//!   SUBSCRIBE  u16 drive letter, u8 mode (1 entries, 2 follow), u64 journal id, i64 next USN
//!              (0, 0 = from now; only read in follow mode)
//! Service to client:
//!   HELLO_OK   u32 magic, u16 version
//!   ERROR      u16 code, text
//!   ENTRIES    u32 count, then each: u64 record, u64 parent record, u32 attributes,
//!              i64 size, i64 date modified (FILETIME, UTC), text name.
//!              A folder always comes before what is in it, the drive's top folder (record 5)
//!              is not sent, and the names in one folder come together.
//!   END        u64 journal id, i64 USN at the start of the read (0, 0 without a journal), u8 source (1 $MFT, 2 enumeration)
//!   FOLLOWING  u64 journal id, i64 USN the changes start from
//!   CHANGES    u32 count, then each: u8 kind (1 look again at a path, 2 renamed), text path,
//!              and for kind 2 text old path; then u64 journal id, i64 USN to resume from
//!   RESCAN     the client's index missed changes (the journal wrapped or was made anew): walk again
//!   NO_JOURNAL the drive keeps no change journal

use crate::ffi::*;
use std::ffi::c_void;

pub const MAGIC: u32 = 0x5342_4350; // "PCBS"
pub const VERSION: u16 = 1;

pub const HELLO: u8 = 1;
pub const SUBSCRIBE: u8 = 2;

pub const HELLO_OK: u8 = 0x81;
pub const ERROR: u8 = 0x82;
pub const ENTRIES: u8 = 0x83;
pub const END: u8 = 0x84;
pub const CHANGES: u8 = 0x85;
pub const FOLLOWING: u8 = 0x86;
pub const RESCAN: u8 = 0x87;
pub const NO_JOURNAL: u8 = 0x88;

pub const MODE_ENTRIES: u8 = 1;
pub const MODE_FOLLOW: u8 = 2;

pub const ERROR_VERSION: u16 = 1;
pub const ERROR_REQUEST: u16 = 2;
pub const ERROR_VOLUME: u16 = 3;
pub const ERROR_NOT_NTFS: u16 = 4;

pub const TOUCH: u8 = 1;
pub const RENAMED: u8 = 2;

/// The client hung up, or the pipe broke.
#[derive(Debug)]
pub struct Gone;

/// One end of a connected pipe.
pub struct Pipe {
    pub handle: Handle,
}

impl Pipe {
    fn write_all(&self, mut bytes: &[u8]) -> Result<(), Gone> {
        while !bytes.is_empty() {
            let mut written = 0u32;
            let size = bytes.len().min(1 << 20) as u32;
            if unsafe { WriteFile(self.handle.0, bytes.as_ptr() as *const c_void, size, &mut written, std::ptr::null_mut()) } == 0 || written == 0 {
                return Err(Gone);
            }
            bytes = &bytes[written as usize..];
        }
        Ok(())
    }

    fn read_exact(&self, buffer: &mut [u8]) -> Result<(), Gone> {
        let mut done = 0;
        while done < buffer.len() {
            let mut got = 0u32;
            let ok = unsafe {
                ReadFile(self.handle.0, buffer[done..].as_mut_ptr() as *mut c_void, (buffer.len() - done) as u32, &mut got, std::ptr::null_mut())
            };
            if ok == 0 || got == 0 {
                return Err(Gone);
            }
            done += got as usize;
        }
        Ok(())
    }

    /// One frame from the client: its type and fields. Requests are small; anything big is refused.
    pub fn read_frame(&self) -> Result<(u8, Vec<u8>), Gone> {
        let mut length = [0u8; 4];
        self.read_exact(&mut length)?;
        let length = u32::from_le_bytes(length) as usize;
        if length == 0 || length > 4096 {
            return Err(Gone);
        }
        let mut body = vec![0u8; length];
        self.read_exact(&mut body)?;
        let kind = body.remove(0);
        Ok((kind, body))
    }
}

/// A frame being put together.
pub struct Frame {
    bytes: Vec<u8>,
}

impl Frame {
    pub fn new(kind: u8) -> Frame {
        let mut bytes = Vec::with_capacity(256);
        bytes.extend_from_slice(&[0, 0, 0, 0, kind]);
        Frame { bytes }
    }

    pub fn with_capacity(kind: u8, capacity: usize) -> Frame {
        let mut bytes = Vec::with_capacity(capacity + 5);
        bytes.extend_from_slice(&[0, 0, 0, 0, kind]);
        Frame { bytes }
    }

    pub fn u8(&mut self, value: u8) -> &mut Frame {
        self.bytes.push(value);
        self
    }

    pub fn u16(&mut self, value: u16) -> &mut Frame {
        self.bytes.extend_from_slice(&value.to_le_bytes());
        self
    }

    pub fn u32(&mut self, value: u32) -> &mut Frame {
        self.bytes.extend_from_slice(&value.to_le_bytes());
        self
    }

    pub fn u64(&mut self, value: u64) -> &mut Frame {
        self.bytes.extend_from_slice(&value.to_le_bytes());
        self
    }

    pub fn i64(&mut self, value: i64) -> &mut Frame {
        self.bytes.extend_from_slice(&value.to_le_bytes());
        self
    }

    pub fn text(&mut self, text: &[u16]) -> &mut Frame {
        let text = &text[..text.len().min(u16::MAX as usize)];
        self.u16(text.len() as u16);
        for unit in text {
            self.bytes.extend_from_slice(&unit.to_le_bytes());
        }
        self
    }

    /// Where a later value goes in (a count known only at the end).
    pub fn mark(&self) -> usize {
        self.bytes.len()
    }

    pub fn put_u32(&mut self, at: usize, value: u32) {
        self.bytes[at..at + 4].copy_from_slice(&value.to_le_bytes());
    }

    pub fn len(&self) -> usize {
        self.bytes.len()
    }

    pub fn send(&mut self, pipe: &Pipe) -> Result<(), Gone> {
        let length = (self.bytes.len() - 4) as u32;
        self.bytes[..4].copy_from_slice(&length.to_le_bytes());
        pipe.write_all(&self.bytes)
    }
}

/// Reading a request's fields.
pub struct Fields<'a> {
    bytes: &'a [u8],
    at: usize,
}

impl<'a> Fields<'a> {
    pub fn new(bytes: &'a [u8]) -> Fields<'a> {
        Fields { bytes, at: 0 }
    }

    fn take<const N: usize>(&mut self) -> Option<[u8; N]> {
        let slice = self.bytes.get(self.at..self.at + N)?;
        self.at += N;
        slice.try_into().ok()
    }

    pub fn u8(&mut self) -> Option<u8> {
        self.take::<1>().map(|b| b[0])
    }

    pub fn u16(&mut self) -> Option<u16> {
        self.take::<2>().map(u16::from_le_bytes)
    }

    pub fn u32(&mut self) -> Option<u32> {
        self.take::<4>().map(u32::from_le_bytes)
    }

    pub fn u64(&mut self) -> Option<u64> {
        self.take::<8>().map(u64::from_le_bytes)
    }

    pub fn i64(&mut self) -> Option<i64> {
        self.take::<8>().map(i64::from_le_bytes)
    }
}

pub fn error(pipe: &Pipe, code: u16, text: &str) -> Result<(), Gone> {
    let text: Vec<u16> = text.encode_utf16().collect();
    Frame::new(ERROR).u16(code).text(&text).send(pipe)
}
