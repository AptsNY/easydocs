namespace EasyDocs.Api.Domain;

public enum OrgRole { Owner, Admin, Member }

public enum DocRole { Owner, Editor, Viewer }

public enum VersionSource { Upload, EditWopi, Import, Merge, Revert, CopyPush }

public enum BranchKind { Main, Concurrent, IncomingPush }
