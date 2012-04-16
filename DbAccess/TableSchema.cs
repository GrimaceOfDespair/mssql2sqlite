using System;
using System.Collections.Generic;
using System.Text;

namespace DbAccess
{
    public class TableSchema
    {
        public string TableName;

        public string TableSchemaName;

        public List<ColumnSchema> Columns;

        public List<string> PrimaryKey;

    	public List<ForeignKeySchema> ForeignKeys;

        public List<IndexSchema> Indexes;
    }
}
