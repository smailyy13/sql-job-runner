USE EDWUTIL;
GO
-- FinishedAt already exists. Replace CALISAN_ROLU with the employee user/role if column permission is needed.
GRANT UPDATE (FinishedAt) ON OPR.EdwJobGeneratorRequests TO [CALISAN_ROLU];
GO
