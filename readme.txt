
Original source: http://www.codeproject.com/Articles/26932/Convert-SQL-Server-DB-to-SQLite-DB


INTRODUCTION

I needed to convert the existing SQL server databases to SQLite databases as part of a DB migration program and did not find any decent free converter to do the job.

This is my attempt to solve the problem. I should warn you though that I did not have much time to test it on too many databases. In any case - the source code is very well documented and easy to understand, so if you do have a problem it should be relatively easy to fix. (Please send me the fixed source code. If you do so, I can update the software so that everybody can enjoy it.)



USING THE CODE

The code is split between a dataaccess project (class library) that contains the conversion code itself and a converter project (WinForms) that drives the conversion code and provides a simple UI for user interaction.

The main class that performs the conversion is the sqlservertosqlite class. It does the conversion by doing the following steps:

Reading the designated SQL server schema and preparing a list of tableschema objects that contain the schema for each and every SQL server table (and associated structures like indexes).
Preparing an empty sqlite database file with the schema that was read from SQL server. In this step, the code may alter few SQL-server types that are not supported directly in sqlite.
Copying rows for each table from the SQL server database to the sqlite database.
Basically, that's it!


POINTS OF INTEREST

In order to read the SQL server DB schema, I was mainly using the pseudo information_schema.table table. You can find more information about it on the Internet if you want.