// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
//! The few Windows functions the service calls, declared by hand so the build needs nothing
//! but the windows-service crate.
#![allow(non_snake_case, clippy::upper_case_acronyms, dead_code)]

use std::ffi::c_void;

pub type HANDLE = *mut c_void;
pub type BOOL = i32;
pub const INVALID_HANDLE_VALUE: HANDLE = -1isize as HANDLE;

pub const GENERIC_READ: u32 = 0x8000_0000;
pub const FILE_SHARE_READ: u32 = 1;
pub const FILE_SHARE_WRITE: u32 = 2;
pub const FILE_SHARE_DELETE: u32 = 4;
pub const OPEN_EXISTING: u32 = 3;
pub const FILE_BEGIN: u32 = 0;
pub const DRIVE_FIXED: u32 = 3;

pub const PIPE_ACCESS_DUPLEX: u32 = 3;
pub const FILE_FLAG_FIRST_PIPE_INSTANCE: u32 = 0x0008_0000;
pub const PIPE_TYPE_BYTE: u32 = 0;
pub const PIPE_READMODE_BYTE: u32 = 0;
pub const PIPE_WAIT: u32 = 0;
pub const PIPE_REJECT_REMOTE_CLIENTS: u32 = 8;
pub const PIPE_UNLIMITED_INSTANCES: u32 = 255;

pub const FSCTL_ENUM_USN_DATA: u32 = 0x0009_00B3;
pub const FSCTL_READ_USN_JOURNAL: u32 = 0x0009_00BB;
pub const FSCTL_QUERY_USN_JOURNAL: u32 = 0x0009_00F4;

pub const ERROR_HANDLE_EOF: u32 = 38;
pub const ERROR_PIPE_CONNECTED: u32 = 535;
pub const ERROR_JOURNAL_DELETE_IN_PROGRESS: u32 = 1178;
pub const ERROR_JOURNAL_NOT_ACTIVE: u32 = 1179;
pub const ERROR_JOURNAL_ENTRY_DELETED: u32 = 1181;

pub const THREAD_MODE_BACKGROUND_BEGIN: i32 = 0x0001_0000;
pub const THREAD_MODE_BACKGROUND_END: i32 = 0x0002_0000;

pub const HKEY_LOCAL_MACHINE: isize = 0x8000_0002u32 as i32 as isize;
pub const RRF_RT_REG_SZ: u32 = 0x0000_0002;
pub const RRF_RT_REG_EXPAND_SZ: u32 = 0x0000_0004;
pub const RRF_NOEXPAND: u32 = 0x1000_0000;

pub const TOKEN_QUERY: u32 = 8;
pub const TOKEN_USER_CLASS: i32 = 1;
pub const SDDL_REVISION_1: u32 = 1;

#[repr(C)]
pub struct SECURITY_ATTRIBUTES {
    pub nLength: u32,
    pub lpSecurityDescriptor: *mut c_void,
    pub bInheritHandle: BOOL,
}

#[repr(C)]
#[derive(Default, Clone, Copy)]
pub struct USN_JOURNAL_DATA_V0 {
    pub UsnJournalID: u64,
    pub FirstUsn: i64,
    pub NextUsn: i64,
    pub LowestValidUsn: i64,
    pub MaxUsn: i64,
    pub MaximumSize: u64,
    pub AllocationDelta: u64,
}

#[repr(C)]
pub struct READ_USN_JOURNAL_DATA_V0 {
    pub StartUsn: i64,
    pub ReasonMask: u32,
    pub ReturnOnlyOnClose: u32,
    pub Timeout: u64,
    pub BytesToWaitFor: u64,
    pub UsnJournalID: u64,
}

#[repr(C)]
pub struct MFT_ENUM_DATA_V0 {
    pub StartFileReferenceNumber: u64,
    pub LowUsn: i64,
    pub HighUsn: i64,
}

#[link(name = "kernel32")]
extern "system" {
    pub fn CreateFileW(name: *const u16, access: u32, share: u32, security: *const c_void, disposition: u32, flags: u32, template: HANDLE) -> HANDLE;
    pub fn ReadFile(file: HANDLE, buffer: *mut c_void, length: u32, read: *mut u32, overlapped: *mut c_void) -> BOOL;
    pub fn WriteFile(file: HANDLE, buffer: *const c_void, length: u32, written: *mut u32, overlapped: *mut c_void) -> BOOL;
    pub fn SetFilePointerEx(file: HANDLE, distance: i64, new_position: *mut i64, method: u32) -> BOOL;
    pub fn DeviceIoControl(device: HANDLE, code: u32, input: *const c_void, input_size: u32, output: *mut c_void, output_size: u32,
                           returned: *mut u32, overlapped: *mut c_void) -> BOOL;
    pub fn CloseHandle(handle: HANDLE) -> BOOL;
    pub fn GetLastError() -> u32;
    pub fn GetVolumeInformationW(root: *const u16, name: *mut u16, name_size: u32, serial: *mut u32, max_component: *mut u32,
                                 flags: *mut u32, fs_name: *mut u16, fs_name_size: u32) -> BOOL;
    pub fn GetDriveTypeW(root: *const u16) -> u32;
    pub fn GetLogicalDrives() -> u32;
    pub fn CreateNamedPipeW(name: *const u16, open_mode: u32, pipe_mode: u32, max_instances: u32, out_size: u32, in_size: u32,
                            timeout: u32, security: *const SECURITY_ATTRIBUTES) -> HANDLE;
    pub fn ConnectNamedPipe(pipe: HANDLE, overlapped: *mut c_void) -> BOOL;
    pub fn DisconnectNamedPipe(pipe: HANDLE) -> BOOL;
    pub fn FlushFileBuffers(file: HANDLE) -> BOOL;
    pub fn LocalFree(memory: *mut c_void) -> *mut c_void;
    pub fn GetCurrentThread() -> HANDLE;
    pub fn SetThreadPriority(thread: HANDLE, priority: i32) -> BOOL;
    pub fn GetCurrentProcess() -> HANDLE;
    pub fn ExpandEnvironmentStringsW(source: *const u16, destination: *mut u16, size: u32) -> u32;
}

#[link(name = "advapi32")]
extern "system" {
    pub fn ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl: *const u16, revision: u32, descriptor: *mut *mut c_void, size: *mut u32) -> BOOL;
    pub fn RegGetValueW(key: isize, subkey: *const u16, value: *const u16, flags: u32, kind: *mut u32, data: *mut c_void, size: *mut u32) -> i32;
    pub fn OpenProcessToken(process: HANDLE, access: u32, token: *mut HANDLE) -> BOOL;
    pub fn GetTokenInformation(token: HANDLE, class: i32, info: *mut c_void, length: u32, returned: *mut u32) -> BOOL;
    pub fn ConvertSidToStringSidW(sid: *mut c_void, text: *mut *mut u16) -> BOOL;
}

/// A text as Windows wants it: UTF-16 ending in a zero.
pub fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

/// A handle that is closed when dropped.
pub struct Handle(pub HANDLE);

impl Handle {
    pub fn valid(&self) -> bool {
        !self.0.is_null() && self.0 != INVALID_HANDLE_VALUE
    }
}

impl Drop for Handle {
    fn drop(&mut self) {
        if self.valid() {
            unsafe { CloseHandle(self.0) };
        }
    }
}

// The handles are only used by one thread at a time.
unsafe impl Send for Handle {}

/// Windows' own text for the last error, as a plain number (the service has nobody to show it to).
pub fn last_error() -> u32 {
    unsafe { GetLastError() }
}

/// Lower this thread's processor and disk priority while it reads a drive, so the PC stays quick.
pub struct Background;

impl Background {
    pub fn begin() -> Background {
        unsafe { SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN) };
        Background
    }
}

impl Drop for Background {
    fn drop(&mut self) {
        unsafe { SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END) };
    }
}
