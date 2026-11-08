//! A MangaDex source for EMMA, ported from the emma-test-plugin reference. It shows a real plugin
//! on the generic contract: a manga is a node, its chapters are its children, a chapter's pages
//! are its children, and a page resolves to an image URL the host fetches itself.
#![allow(missing_docs)]

pub mod mapping;
pub mod util;

#[cfg(target_arch = "wasm32")]
mod component;
